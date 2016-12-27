using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text;
using CSharpLua.LuaAst;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;

namespace CSharpLua {
    public sealed partial class LuaSyntaxNodeTransfor : CSharpSyntaxVisitor<LuaSyntaxNode> {
        private SemanticModel semanticModel_;
        private XmlMetaProvider xmlMetaProvider_;

        private Stack<LuaTypeDeclarationSyntax> typeDeclarations_ = new Stack<LuaTypeDeclarationSyntax>();
        private Stack<LuaFunctionExpressSyntax> functions_ = new Stack<LuaFunctionExpressSyntax>();
        private Stack<LuaSwitchAdapterStatementSyntax> switchs_ = new Stack<LuaSwitchAdapterStatementSyntax>();
        private Stack<LuaBlockSyntax> blocks_ = new Stack<LuaBlockSyntax>();

        private static readonly Dictionary<string, string> operatorTokenMapps_ = new Dictionary<string, string>() {
            ["!="] = "~=",
            ["!"] = LuaSyntaxNode.Tokens.Not,
            ["&&"] = LuaSyntaxNode.Tokens.And,
            ["||"] = LuaSyntaxNode.Tokens.Or,
            ["??"] = LuaSyntaxNode.Tokens.Or,
        };

        public LuaSyntaxNodeTransfor(SemanticModel semanticModel, XmlMetaProvider xmlMetaProvider) {
            semanticModel_ = semanticModel;
            xmlMetaProvider_ = xmlMetaProvider;
        }

        private static string GetOperatorToken(string operatorToken) {
            return operatorTokenMapps_.GetOrDefault(operatorToken, operatorToken);
        }

        private LuaTypeDeclarationSyntax CurType {
            get {
                return typeDeclarations_.Peek();
            }
        }

        private LuaFunctionExpressSyntax CurFunction {
            get {
                return functions_.Peek();
            }
        }

        private LuaBlockSyntax CurBlock {
            get {
                return blocks_.Peek();
            }
        }

        public override LuaSyntaxNode VisitCompilationUnit(CompilationUnitSyntax node) {
            LuaCompilationUnitSyntax compilationUnit = new LuaCompilationUnitSyntax() { FilePath = node.SyntaxTree.FilePath };
            foreach(var member in node.Members) {
                LuaStatementSyntax memberNode = (LuaStatementSyntax)member.Accept(this);
                var typeDeclaration = memberNode as LuaTypeDeclarationSyntax;
                if(typeDeclaration != null) {
                    compilationUnit.AddTypeDeclaration(typeDeclaration);
                }
                else {
                    compilationUnit.Statements.Add(memberNode);
                }
            }
            return compilationUnit;
        }

        public override LuaSyntaxNode VisitNamespaceDeclaration(NamespaceDeclarationSyntax node) {
            LuaIdentifierNameSyntax name = (LuaIdentifierNameSyntax)node.Name.Accept(this);
            LuaNamespaceDeclarationSyntax namespaceDeclaration = new LuaNamespaceDeclarationSyntax(name);
            foreach(var member in node.Members) {
                var memberNode = (LuaTypeDeclarationSyntax)member.Accept(this);
                namespaceDeclaration.Add(memberNode);
            }
            return namespaceDeclaration;
        }

        private void VisitTypeDeclaration(TypeDeclarationSyntax node, LuaTypeDeclarationSyntax typeDeclaration) {
            typeDeclarations_.Push(typeDeclaration);
            if(node.TypeParameterList != null) {
                foreach(var typeParameter in node.TypeParameterList.Parameters) {
                    var typeIdentifier = (LuaIdentifierNameSyntax)typeParameter.Accept(this);
                    typeDeclaration.AddTypeIdentifier(typeIdentifier);
                }
            }
            if(node.BaseList != null) {
                List<LuaIdentifierNameSyntax> baseTypes = new List<LuaIdentifierNameSyntax>();
                foreach(var baseType in node.BaseList.Types) {
                    var baseTypeName = (LuaIdentifierNameSyntax)baseType.Accept(this);
                    baseTypes.Add(baseTypeName);
                }
                typeDeclaration.AddBaseTypes(baseTypes);
            }
            foreach(var member in node.Members) {
                var newMember = member.Accept(this);
                SyntaxKind kind = member.Kind();
                if(kind >= SyntaxKind.ClassDeclaration && kind <= SyntaxKind.EnumDeclaration) {
                    typeDeclaration.Add((LuaStatementSyntax)newMember);
                }
            }
            typeDeclarations_.Pop();
        }

        public override LuaSyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node) {
            LuaIdentifierNameSyntax name = new LuaIdentifierNameSyntax(node.Identifier.ValueText);
            LuaClassDeclarationSyntax classDeclaration = new LuaClassDeclarationSyntax(name);
            VisitTypeDeclaration(node, classDeclaration);
            return classDeclaration;
        }

        public override LuaSyntaxNode VisitStructDeclaration(StructDeclarationSyntax node) {
            LuaIdentifierNameSyntax name = new LuaIdentifierNameSyntax(node.Identifier.ValueText);
            LuaStructDeclarationSyntax structDeclaration = new LuaStructDeclarationSyntax(name);
            VisitTypeDeclaration(node, structDeclaration);
            return structDeclaration;
        }

        public override LuaSyntaxNode VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) {
            LuaIdentifierNameSyntax name = new LuaIdentifierNameSyntax(node.Identifier.ValueText);
            LuaInterfaceDeclarationSyntax interfaceDeclaration = new LuaInterfaceDeclarationSyntax(name);
            VisitTypeDeclaration(node, interfaceDeclaration);
            return interfaceDeclaration;
        }

        public override LuaSyntaxNode VisitEnumDeclaration(EnumDeclarationSyntax node) {
            LuaIdentifierNameSyntax name = new LuaIdentifierNameSyntax(node.Identifier.ValueText);
            LuaEnumDeclarationSyntax enumDeclaration = new LuaEnumDeclarationSyntax(name);
            typeDeclarations_.Push(enumDeclaration);
            foreach(var member in node.Members) {
                member.Accept(this);
            }
            typeDeclarations_.Pop();
            return enumDeclaration;
        }

        private void VisitYield(MethodDeclarationSyntax node, LuaFunctionExpressSyntax function) {
            Contract.Assert(function.HasYield);

            var nameSyntax = (SimpleNameSyntax)node.ReturnType;
            string name = LuaSyntaxNode.Tokens.Yield + nameSyntax.Identifier.ValueText;
            LuaMemberAccessExpressionSyntax memberAccess = new LuaMemberAccessExpressionSyntax(LuaIdentifierNameSyntax.System, new LuaIdentifierNameSyntax(name));
            LuaInvocationExpressionSyntax invokeExpression = new LuaInvocationExpressionSyntax(memberAccess);
            LuaFunctionExpressSyntax wrapFunction = new LuaFunctionExpressSyntax();

            var parameters = function.ParameterList.Parameters;
            wrapFunction.ParameterList.Parameters.AddRange(parameters);
            wrapFunction.Body.Statements.AddRange(function.Body.Statements);
            invokeExpression.AddArgument(wrapFunction);
            if(node.ReturnType.IsKind(SyntaxKind.GenericName)) {
                var genericNameSyntax = (GenericNameSyntax)nameSyntax;
                var typeName = genericNameSyntax.TypeArgumentList.Arguments.First();
                var expression = (LuaExpressionSyntax)typeName.Accept(this);
                invokeExpression.AddArgument(expression);
            }
            else {
                invokeExpression.AddArgument(LuaIdentifierNameSyntax.Object);
            }
            invokeExpression.ArgumentList.Arguments.AddRange(parameters.Select(i => new LuaArgumentSyntax(i.Identifier)));

            LuaReturnStatementSyntax returnStatement = new LuaReturnStatementSyntax(invokeExpression);
            function.Body.Statements.Clear();
            function.Body.Statements.Add(returnStatement);
        }

        public override LuaSyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node) {
            LuaIdentifierNameSyntax name = new LuaIdentifierNameSyntax(node.Identifier.ValueText);
            LuaFunctionExpressSyntax function = new LuaFunctionExpressSyntax();
            functions_.Push(function);
            if(!node.Modifiers.IsStatic()) {
                function.AddParameter(LuaIdentifierNameSyntax.This);
            }

            var parameterList = (LuaParameterListSyntax)node.ParameterList.Accept(this);
            function.ParameterList.Parameters.AddRange(parameterList.Parameters);
            if(node.TypeParameterList != null) {
                foreach(var typeParameter in node.TypeParameterList.Parameters) {
                    var typeName = (LuaIdentifierNameSyntax)typeParameter.Accept(this);
                    function.AddParameter(typeName);
                }
            }

            LuaBlockSyntax block = (LuaBlockSyntax)node.Body.Accept(this);
            function.Body.Statements.AddRange(block.Statements);
            if(function.HasYield) {
                VisitYield(node, function);
            }
            functions_.Pop();
            CurType.AddMethod(name, function, node.Modifiers.IsPrivate());
            return function;
        }

        private static string GetPredefinedTypeDefaultValue(string name) {
            switch(name) {
                case "byte":
                case "sbyte":
                case "short":
                case "ushort":
                case "int":
                case "uint":
                case "long":
                case "ulong":
                    return 0.ToString();
                case "float":
                case "double":
                    return 0.0.ToString();
                case "bool":
                    return false.ToString();
                default:
                    return null;
            }
        }

        private LuaIdentifierNameSyntax GetTempIdentifier(SyntaxNode node) {
            int index = CurFunction.TempIndex;
            string name = LuaSyntaxNode.TempIdentifiers.GetOrDefault(index);
            if(name == null) {
                throw new CompilationErrorException($"{node.GetLocationString()} : Your code is startling, {LuaSyntaxNode.TempIdentifiers.Length} " 
                    + "temporary variables is not enough, please refactor your code.");
            }
            ++CurFunction.TempIndex;
            return new LuaIdentifierNameSyntax(name);
        }

        private LuaInvocationExpressionSyntax BuildDefaultValueExpression(TypeSyntax type) {
            var identifierName = (LuaIdentifierNameSyntax)type.Accept(this);
            return new LuaInvocationExpressionSyntax(new LuaMemberAccessExpressionSyntax(identifierName, LuaIdentifierNameSyntax.Default));
        }

        private void VisitBaseFieldDeclarationSyntax(BaseFieldDeclarationSyntax node) {
            if(!node.Modifiers.IsConst()) {
                bool isStatic = node.Modifiers.IsStatic();
                bool isPrivate = node.Modifiers.IsPrivate();
                bool isReadOnly = node.Modifiers.IsReadOnly();
                var type = node.Declaration.Type;
                ITypeSymbol typeSymbol = (ITypeSymbol)semanticModel_.GetSymbolInfo(type).Symbol;
                bool isImmutable = typeSymbol.IsImmutable();
                foreach(var variable in node.Declaration.Variables) {
                    if(node.IsKind(SyntaxKind.EventFieldDeclaration)) {
                        var eventSymbol = (IEventSymbol)semanticModel_.GetDeclaredSymbol(variable);
                        if(eventSymbol.IsOverridable() || eventSymbol.IsInterfaceImplementation()) {
                            bool valueIsLiteral;
                            LuaExpressionSyntax valueExpression = GetFieldValueExpression(type, typeSymbol, variable.Initializer?.Value, out valueIsLiteral);
                            CurType.AddEvent(variable.Identifier.ValueText, valueExpression, isImmutable && valueIsLiteral, isStatic, isPrivate);
                            continue;
                        }
                    }
                    AddField(type, typeSymbol, variable.Identifier, variable.Initializer?.Value, isImmutable, isStatic, isPrivate, isReadOnly);
                }
            }
        }

        public override LuaSyntaxNode VisitFieldDeclaration(FieldDeclarationSyntax node) {
            VisitBaseFieldDeclarationSyntax(node);
            return base.VisitFieldDeclaration(node);
        }

        private LuaExpressionSyntax GetFieldValueExpression(TypeSyntax type, ITypeSymbol typeSymbol, ExpressionSyntax expression, out bool valueIsLiteral) {
            LuaExpressionSyntax valueExpression = null;
            valueIsLiteral = false;
            if(expression != null) {
                valueExpression = (LuaExpressionSyntax)expression.Accept(this);
                valueIsLiteral = expression is LiteralExpressionSyntax;
            }
            if(valueExpression == null) {
                if(typeSymbol.IsValueType) {
                    if(typeSymbol.IsDefinition) {
                        string valueText = GetPredefinedTypeDefaultValue(typeSymbol.ToString());
                        if(valueText != null) {
                            valueExpression = new LuaIdentifierNameSyntax(valueText);
                        }
                        else {
                            valueExpression = BuildDefaultValueExpression(type);
                        }
                        valueIsLiteral = true;
                    }
                    else {
                        valueExpression = BuildDefaultValueExpression(type);
                    }
                }
            }
            return valueExpression;
        }

        private void AddField(TypeSyntax type, ITypeSymbol typeSymbol, SyntaxToken identifier, ExpressionSyntax expression, bool isImmutable, bool isStatic, bool isPrivate, bool isReadOnly) {
            LuaIdentifierNameSyntax name = new LuaIdentifierNameSyntax(identifier.ValueText);
            bool valueIsLiteral;
            LuaExpressionSyntax valueExpression = GetFieldValueExpression(type, typeSymbol, expression, out valueIsLiteral);
            CurType.AddField(name, valueExpression, isImmutable && valueIsLiteral, isStatic, isPrivate, isReadOnly);
        }

        public override LuaSyntaxNode VisitPropertyDeclaration(PropertyDeclarationSyntax node) {
            bool isStatic = node.Modifiers.IsStatic();
            bool isPrivate = node.Modifiers.IsPrivate();
            bool hasGet = false;
            bool hasSet = false;
            if(node.AccessorList != null) {
                foreach(var accessor in node.AccessorList.Accessors) {
                    if(accessor.Body != null) {
                        var block = (LuaBlockSyntax)accessor.Body.Accept(this);
                        LuaFunctionExpressSyntax functionExpress = new LuaFunctionExpressSyntax();
                        if(!isStatic) {
                            functionExpress.AddParameter(LuaIdentifierNameSyntax.This);
                        }
                        functionExpress.Body.Statements.AddRange(block.Statements);
                        LuaPropertyOrEventIdentifierNameSyntax name = new LuaPropertyOrEventIdentifierNameSyntax(true, node.Identifier.ValueText);
                        CurType.AddMethod(name, functionExpress, isPrivate);
                        if(accessor.IsKind(SyntaxKind.GetAccessorDeclaration)) {
                            Contract.Assert(!hasGet);
                            hasGet = true;
                        }
                        else {
                            Contract.Assert(!hasSet);
                            functionExpress.AddParameter(LuaIdentifierNameSyntax.Value);
                            name.IsGetOrAdd = false;
                            hasSet = true;
                        }
                    }
                }
            }
            else {
                Contract.Assert(!hasGet);
                LuaPropertyOrEventIdentifierNameSyntax name = new LuaPropertyOrEventIdentifierNameSyntax(true, node.Identifier.ValueText);
                var expression = (LuaExpressionSyntax)node.ExpressionBody.Expression.Accept(this);
                LuaFunctionExpressSyntax functionExpress = new LuaFunctionExpressSyntax();
                if(!isStatic) {
                    functionExpress.AddParameter(LuaIdentifierNameSyntax.This);
                }
                LuaReturnStatementSyntax returnStatement = new LuaReturnStatementSyntax(expression);
                functionExpress.Body.Statements.Add(returnStatement);
                CurType.AddMethod(name, functionExpress, isPrivate);
                hasGet = true;
            }

            if(!hasGet && !hasSet) {
                if(!node.Parent.IsKind(SyntaxKind.InterfaceDeclaration)) {
                    var type = node.Type;
                    ITypeSymbol typeSymbol = (ITypeSymbol)semanticModel_.GetSymbolInfo(type).Symbol;
                    bool isImmutable = typeSymbol.IsImmutable();
                    if(isStatic) {
                        bool isReadOnly = node.AccessorList.Accessors.Count == 1 && node.AccessorList.Accessors[0].Body == null;
                        AddField(type, typeSymbol, node.Identifier, node.Initializer?.Value, isImmutable, isStatic, isPrivate, isReadOnly);
                    }
                    else {
                        bool isAuto = semanticModel_.GetDeclaredSymbol(node).IsPropertyField();
                        if(isAuto) {
                            bool isReadOnly = node.AccessorList.Accessors.Count == 1 && node.AccessorList.Accessors[0].Body == null;
                            AddField(type, typeSymbol, node.Identifier, node.Initializer?.Value, isImmutable, isStatic, isPrivate, isReadOnly);
                        }
                        else {
                            bool valueIsLiteral;
                            LuaExpressionSyntax valueExpression = GetFieldValueExpression(type, typeSymbol, node.Initializer?.Value, out valueIsLiteral);
                            CurType.AddProperty(node.Identifier.ValueText, valueExpression, isImmutable && valueIsLiteral, isStatic, isPrivate);
                        }
                    }
                }
            }

            return base.VisitPropertyDeclaration(node);
        }

        public override LuaSyntaxNode VisitEventDeclaration(EventDeclarationSyntax node) {
            bool isStatic = node.Modifiers.IsStatic();
            bool isPrivate = node.Modifiers.IsPrivate();
            foreach(var accessor in node.AccessorList.Accessors) {
                var block = (LuaBlockSyntax)accessor.Body.Accept(this);
                LuaFunctionExpressSyntax functionExpress = new LuaFunctionExpressSyntax();
                if(!isStatic) {
                    functionExpress.AddParameter(LuaIdentifierNameSyntax.This);
                }
                functionExpress.AddParameter(LuaIdentifierNameSyntax.Value);
                functionExpress.Body.Statements.AddRange(block.Statements);
                LuaPropertyOrEventIdentifierNameSyntax name = new LuaPropertyOrEventIdentifierNameSyntax(false, node.Identifier.ValueText);
                CurType.AddMethod(name, functionExpress, isPrivate);
                if(accessor.IsKind(SyntaxKind.RemoveAccessorDeclaration)) {
                    name.IsGetOrAdd = false;
                }
            }

            return base.VisitEventDeclaration(node);
        }

        public override LuaSyntaxNode VisitEventFieldDeclaration(EventFieldDeclarationSyntax node) {
            VisitBaseFieldDeclarationSyntax(node);
            return base.VisitEventFieldDeclaration(node);
        }

        public override LuaSyntaxNode VisitParameterList(ParameterListSyntax node) {
            LuaParameterListSyntax parameterList = new LuaParameterListSyntax();
            foreach(var parameter in node.Parameters) {
                var newNode = (LuaParameterSyntax)parameter.Accept(this);
                parameterList.Parameters.Add(newNode);
            }
            return parameterList;
        }

        public override LuaSyntaxNode VisitParameter(ParameterSyntax node) {
            LuaIdentifierNameSyntax identifier = new LuaIdentifierNameSyntax(node.Identifier.ValueText);
            return new LuaParameterSyntax(identifier);
        }

        private sealed class BlockCommonNode : IComparable<BlockCommonNode> {
            const int kCommentCharCount = 2;
            public SyntaxTrivia Comment { get; }
            public StatementSyntax Statement { get; }
            public FileLinePositionSpan LineSpan { get; }

            public BlockCommonNode(SyntaxTrivia comment) {
                Comment = comment;
                LineSpan = comment.SyntaxTree.GetLineSpan(comment.Span);
            }

            public BlockCommonNode(StatementSyntax statement) {
                Statement = statement;
                LineSpan = statement.SyntaxTree.GetLineSpan(statement.Span);
            }

            public int CompareTo(BlockCommonNode other) {
                return LineSpan.StartLinePosition.CompareTo(other.LineSpan.StartLinePosition);
            }

            public void Visit(LuaSyntaxNodeTransfor transfor, LuaBlockSyntax block, ref int lastLine) {
                if(lastLine != -1) {
                    int count = LineSpan.StartLinePosition.Line - lastLine - 1;
                    if(count > 0) {
                        block.Statements.Add(new LuaBlankLinesStatement(count));
                    }
                }

                if(Statement != null) {
                    LuaStatementSyntax statementNode = (LuaStatementSyntax)Statement.Accept(transfor);
                    block.Statements.Add(statementNode);
                }
                else {
                    string content = Comment.ToString();
                    if(Comment.IsKind(SyntaxKind.SingleLineCommentTrivia)) {
                        string commentContent = content.Substring(kCommentCharCount);
                        LuaShortCommentStatement singleComment = new LuaShortCommentStatement(commentContent);
                        block.Statements.Add(singleComment);
                    }
                    else {
                        string commentContent = content.Substring(kCommentCharCount, content.Length - kCommentCharCount - kCommentCharCount);
                        LuaLongCommentStatement longComment = new LuaLongCommentStatement(commentContent);
                        block.Statements.Add(longComment);
                    }
                }

                lastLine = LineSpan.EndLinePosition.Line;
            }
        }

        public override LuaSyntaxNode VisitBlock(BlockSyntax node) {
            LuaBlockSyntax block = new LuaBlockSyntax();
            blocks_.Push(block);

            var comments = node.DescendantTrivia().Where(i => i.IsKind(SyntaxKind.SingleLineCommentTrivia) || i.IsKind(SyntaxKind.MultiLineCommentTrivia));
            List<BlockCommonNode> commonNodes = new List<BlockCommonNode>();
            commonNodes.AddRange(comments.Select(i => new BlockCommonNode(i)));
            bool hasComments = commonNodes.Count > 0;
            commonNodes.AddRange(node.Statements.Select(i => new BlockCommonNode(i)));
            if(hasComments) {
                commonNodes.Sort();
            }

            int lastLine = -1;
            foreach(var common in commonNodes) {
                common.Visit(this, block, ref lastLine);
            }

            blocks_.Pop();
            SyntaxKind kind = node.Parent.Kind();
            if(kind == SyntaxKind.Block || kind == SyntaxKind.SwitchSection) {
                return new LuaBlockBlockSyntax(block);
            }
            else {
                return block;
            }
        }

        public override LuaSyntaxNode VisitReturnStatement(ReturnStatementSyntax node) {
            if(node.Expression != null) {
                var expression = (LuaExpressionSyntax)node.Expression.Accept(this);
                return new LuaReturnStatementSyntax(expression);
            }
            return new LuaReturnStatementSyntax();
        }

        public override LuaSyntaxNode VisitExpressionStatement(ExpressionStatementSyntax node) {
            LuaExpressionSyntax expressionNode = (LuaExpressionSyntax)node.Expression.Accept(this);
            return new LuaExpressionStatementSyntax(expressionNode);
        }

        private LuaExpressionSyntax BuildLuaAssignmentExpression(LuaExpressionSyntax left, LuaExpressionSyntax right) {
            var propertyAdapter = left as LuaPropertyAdapterExpressionSyntax;
            if(propertyAdapter != null) {
                propertyAdapter.IsGet = false;
                propertyAdapter.InvocationExpression.AddArgument(right);
                return propertyAdapter;
            }
            else {
                return new LuaAssignmentExpressionSyntax(left, right);
            }
        }

        public override LuaSyntaxNode VisitAssignmentExpression(AssignmentExpressionSyntax node) {
            if(node.Right.Kind() != SyntaxKind.SimpleAssignmentExpression ) {
                var left = (LuaExpressionSyntax)node.Left.Accept(this);
                var right = (LuaExpressionSyntax)node.Right.Accept(this);
                return BuildLuaAssignmentExpression(left, right);
            }
            else {
                List<LuaExpressionSyntax> assignments = new List<LuaExpressionSyntax>();
                var leftExpression = node.Left;
                var rightExpression = node.Right;

                while(true) {
                    var left = (LuaExpressionSyntax)leftExpression.Accept(this);
                    var assignmentRight = rightExpression as AssignmentExpressionSyntax;
                    if(assignmentRight == null) {
                        var right = (LuaExpressionSyntax)rightExpression.Accept(this);
                        assignments.Add(BuildLuaAssignmentExpression(left, right));
                        break;
                    }
                    else {
                        var right = (LuaExpressionSyntax)assignmentRight.Left.Accept(this);
                        assignments.Add(BuildLuaAssignmentExpression(left, right));
                        leftExpression = assignmentRight.Left;
                        rightExpression = assignmentRight.Right;
                    }
                }

                assignments.Reverse();
                LuaLineMultipleExpressionSyntax multipleAssignment = new LuaLineMultipleExpressionSyntax();
                multipleAssignment.Assignments.AddRange(assignments);
                return multipleAssignment;
            }
        }

        private LuaSyntaxNode BuildInvokeRefOrOut(InvocationExpressionSyntax node, LuaInvocationExpressionSyntax invocation, List<LuaArgumentSyntax> refOrOutArguments) {
            if(node.Parent.IsKind(SyntaxKind.ExpressionStatement)) {
                LuaMultipleAssignmentExpressionSyntax multipleAssignment = new LuaMultipleAssignmentExpressionSyntax();
                SymbolInfo symbolInfo = semanticModel_.GetSymbolInfo(node);
                IMethodSymbol symbol = (IMethodSymbol)symbolInfo.Symbol;
                if(!symbol.ReturnsVoid) {
                    var temp = GetTempIdentifier(node);
                    CurBlock.Statements.Add(new LuaLocalVariableDeclaratorSyntax(new LuaVariableDeclaratorSyntax(temp)));
                    multipleAssignment.Lefts.Add(temp);
                }
                multipleAssignment.Lefts.AddRange(refOrOutArguments.Select(i => i.Expression));
                multipleAssignment.Rights.Add(invocation);
                return multipleAssignment;
            }
            else {
                var temp = GetTempIdentifier(node);
                LuaMultipleAssignmentExpressionSyntax multipleAssignment = new LuaMultipleAssignmentExpressionSyntax();
                multipleAssignment.Lefts.Add(temp);
                multipleAssignment.Lefts.AddRange(refOrOutArguments.Select(i => i.Expression));
                multipleAssignment.Rights.Add(invocation);

                CurBlock.Statements.Add(new LuaLocalVariableDeclaratorSyntax(new LuaVariableDeclaratorSyntax(temp)));
                CurBlock.Statements.Add(new LuaExpressionStatementSyntax(multipleAssignment));
                return temp;
            }
        }

        public override LuaSyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node) {
            List<LuaArgumentSyntax> arguments = new List<LuaArgumentSyntax>();
            List<LuaArgumentSyntax> refOrOutArguments = new List<LuaArgumentSyntax>();

            foreach(var argument in node.ArgumentList.Arguments) {
                var luaArgument = (LuaArgumentSyntax)argument.Accept(this);
                arguments.Add(luaArgument);
                if(argument.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)) {
                    refOrOutArguments.Add(luaArgument);
                }
            }

            var expression = (LuaExpressionSyntax)node.Expression.Accept(this);
            LuaInvocationExpressionSyntax invocation;
            var symbol = (IMethodSymbol)semanticModel_.GetSymbolInfo(node).Symbol;
            if(!symbol.IsExtensionMethod) {
                invocation = new LuaInvocationExpressionSyntax(expression);
                if(expression is LuaInternalMethodIdentifierNameSyntax) {
                    invocation.AddArgument(LuaIdentifierNameSyntax.This);
                }
            }
            else {
                LuaMemberAccessExpressionSyntax memberAccess = (LuaMemberAccessExpressionSyntax)expression;
                IMethodSymbol reducedFrom = symbol.ReducedFrom;
                string name = reducedFrom.ContainingType.ToString() + '.' + reducedFrom.Name;
                invocation = new LuaInvocationExpressionSyntax(new LuaIdentifierNameSyntax(name));
                invocation.AddArgument(memberAccess.Expression);
            }
            invocation.ArgumentList.Arguments.AddRange(arguments);
            var methodSybol = (IMethodSymbol)semanticModel_.GetSymbolInfo(node).Symbol;
            if(methodSybol.TypeArguments.Length > 0) {
                int optionalCount = methodSybol.Parameters.Length - node.ArgumentList.Arguments.Count;
                while(optionalCount > 0) {
                    invocation.AddArgument(LuaIdentifierNameSyntax.Nil);
                    --optionalCount;
                }
                foreach(var typeArgument in methodSybol.TypeArguments) {
                    string typeName = xmlMetaProvider_.GetTypeMapName(typeArgument);
                    invocation.AddArgument(new LuaIdentifierNameSyntax(typeName));
                }
            }
            if(refOrOutArguments.Count > 0) {
                return BuildInvokeRefOrOut(node, invocation, refOrOutArguments);
            }
            return invocation;
        }

        public override LuaSyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node) {
            var expression = (LuaExpressionSyntax)node.Expression.Accept(this);
            SymbolInfo symbolInfo = semanticModel_.GetSymbolInfo(node);
            ISymbol symbol = symbolInfo.Symbol;
            if(symbol.Kind != SymbolKind.Property) {
                if(symbol.Kind == SymbolKind.Field) {
                    IFieldSymbol fieldSymbol = (IFieldSymbol)symbol;
                    if(fieldSymbol.HasConstantValue) {
                        return VisitConstIdentifier(fieldSymbol.ConstantValue);
                    }
                }
                var identifier = (LuaIdentifierNameSyntax)node.Name.Accept(this);
                return new LuaMemberAccessExpressionSyntax(expression, identifier, !symbol.IsStatic && symbol.Kind == SymbolKind.Method);
            }
            else {
                var propertyIdentifier = (LuaExpressionSyntax)node.Name.Accept(this);
                var propertyAdapter = propertyIdentifier as LuaPropertyAdapterExpressionSyntax;
                if(propertyAdapter != null) {
                    var memberAccessExpression = new LuaMemberAccessExpressionSyntax(expression, propertyAdapter.InvocationExpression.Expression, !symbol.IsStatic);
                    propertyAdapter.Update(memberAccessExpression);
                    return propertyAdapter;
                }
                else {
                    return new LuaMemberAccessExpressionSyntax(expression, propertyIdentifier);
                }
            }
        }

        private string BuildStaticFieldName(ISymbol symbol, bool isReadOnly, IdentifierNameSyntax node) {
            Contract.Assert(symbol.IsStatic);
            string name;
            if(symbol.DeclaredAccessibility == Accessibility.Private) {
                name = symbol.Name;
            }
            else {
                if(isReadOnly) {
                    name = symbol.Name;
                    if(node.Parent.IsKind(SyntaxKind.SimpleAssignmentExpression)) {
                        AssignmentExpressionSyntax assignmentExpression = (AssignmentExpressionSyntax)node.Parent;
                        if(assignmentExpression.Left == node) {
                            CurType.AddStaticReadOnlyAssignmentName(name);
                        }
                    }
                }
                else {
                    var constructor = CurFunction as LuaConstructorAdapterExpressSyntax;
                    if(constructor != null && constructor.IsStaticCtor) {
                        name = LuaSyntaxNode.Tokens.This + '.' + symbol.Name;
                    }
                    else {
                        if(IsInternalNode(node)) {
                            name = symbol.ToString();
                        }
                        else {
                            name = symbol.Name;
                        }
                    }
                }
            }
            return name;
        }

        private bool IsInternalNode(NameSyntax node) {
            bool isInternal = false;
            MemberAccessExpressionSyntax parent = null;
            if(node.Parent.IsKind(SyntaxKind.SimpleMemberAccessExpression)) {
                parent = (MemberAccessExpressionSyntax)node.Parent;
            }
            if(parent != null) {
                if(parent.Expression == node) {
                    isInternal = true;
                }
            }
            else {
                isInternal = true;
            }
            return isInternal;
        }

        private LuaExpressionSyntax VisitFieldOrEventIdentifierName(IdentifierNameSyntax node, ISymbol symbol, bool isProperty) {
            string name;
            bool isField, isReadOnly;
            if(isProperty) {
                var propertySymbol = (IPropertySymbol)symbol;
                isField = propertySymbol.IsPropertyField();
                isReadOnly = propertySymbol.IsReadOnly;
            }
            else {
                var eventSymbol = (IEventSymbol)symbol;
                isField = eventSymbol.IsEventFiled();
                isReadOnly = false;
            }

            if(symbol.IsStatic) {
                if(isField) {
                    name = BuildStaticFieldName(symbol, isReadOnly, node);
                }
                else {
                    return new LuaPropertyAdapterExpressionSyntax(new LuaPropertyOrEventIdentifierNameSyntax(isProperty, symbol.Name));
                }
            }
            else {
                if(isField) {
                    if(IsInternalNode(node)) {
                        name = LuaSyntaxNode.Tokens.This + '.' + symbol.Name;
                    }
                    else {
                        name = symbol.Name;
                    }
                }
                else {
                    if(IsInternalNode(node)) {
                        if(symbol.IsOverridable() && !symbol.ContainingType.IsSealed) {
                            LuaPropertyOrEventIdentifierNameSyntax identifierName = new LuaPropertyOrEventIdentifierNameSyntax(isProperty, symbol.Name);
                            LuaMemberAccessExpressionSyntax memberAccess = new LuaMemberAccessExpressionSyntax(LuaIdentifierNameSyntax.This, identifierName, true);
                            return new LuaPropertyAdapterExpressionSyntax(memberAccess, identifierName);
                        }
                        else {
                            var propertyAdapter = new LuaPropertyAdapterExpressionSyntax(new LuaPropertyOrEventIdentifierNameSyntax(isProperty, symbol.Name));
                            propertyAdapter.InvocationExpression.AddArgument(LuaIdentifierNameSyntax.This);
                            return propertyAdapter;
                        }
                    }
                    else {
                        return new LuaPropertyAdapterExpressionSyntax(new LuaPropertyOrEventIdentifierNameSyntax(isProperty, symbol.Name));
                    }
                }
            }
            return new LuaIdentifierNameSyntax(name);
        }

        private LuaSyntaxNode VisitConstIdentifier(object constantValue) {
            if(constantValue != null) {
                var code = Type.GetTypeCode(constantValue.GetType());
                switch(code) {
                    case TypeCode.Char: {
                            return new LuaCharacterLiteralExpression((char)constantValue);
                        }
                    case TypeCode.String: {
                            return new LuaStringLiteralExpressionSyntax(new LuaIdentifierNameSyntax((string)constantValue));
                        }
                    default: {
                            return new LuaIdentifierNameSyntax(constantValue.ToString());
                        }
                }
            }
            else {
                return LuaIdentifierNameSyntax.Nil;
            }
        }

        private LuaExpressionSyntax GetMethodNameExpression(IMethodSymbol symbol, NameSyntax node) {
            string name;
            string methodName = xmlMetaProvider_.GetMethodMapName(symbol);
            if(symbol.IsStatic) {
                name = methodName;
            }
            else {
                if(IsInternalNode(node)) {
                    if(symbol.IsOverridable() && !symbol.ContainingType.IsSealed) {
                        LuaIdentifierNameSyntax identifierName = new LuaIdentifierNameSyntax(methodName);
                        LuaMemberAccessExpressionSyntax memberAccess = new LuaMemberAccessExpressionSyntax(LuaIdentifierNameSyntax.This, identifierName, true);
                        return memberAccess;
                    }
                    else {
                        return new LuaInternalMethodIdentifierNameSyntax(methodName);
                    }
                }
                else {
                    name = methodName;
                }
            }
            return new LuaIdentifierNameSyntax(name);
        }

        public override LuaSyntaxNode VisitIdentifierName(IdentifierNameSyntax node) {
            SymbolInfo symbolInfo = semanticModel_.GetSymbolInfo(node);
            ISymbol symbol = symbolInfo.Symbol;
            string name;
            switch(symbol.Kind) {
                case SymbolKind.Local:
                case SymbolKind.Parameter:
                case SymbolKind.TypeParameter:
                case SymbolKind.Label: {
                        name = symbol.Name;
                        break;
                    }
                case SymbolKind.NamedType: {
                        name = symbol.ToString();
                        break;
                    }
                case SymbolKind.Field: {
                        if(symbol.IsStatic) {
                            var fieldSymbol = (IFieldSymbol)symbol;
                            if(fieldSymbol.HasConstantValue) {
                                return VisitConstIdentifier(fieldSymbol.ConstantValue);
                            }
                            else {
                                name = BuildStaticFieldName(symbol, fieldSymbol.IsReadOnly, node);
                            }
                        }
                        else {
                            if(IsInternalNode(node)) {
                                name = LuaSyntaxNode.Tokens.This + '.' + symbol.Name;
                            }
                            else {
                                name = symbol.Name;
                            }
                        }
                        break;
                    }
                case SymbolKind.Method: {
                        return GetMethodNameExpression((IMethodSymbol)symbol, node);
                    }
                case SymbolKind.Property: {
                        return VisitFieldOrEventIdentifierName(node, symbol, true);
                    }
                case SymbolKind.Event: {
                        return VisitFieldOrEventIdentifierName(node, symbol, false);
                    }
                default: {
                        throw new NotSupportedException();
                    }
            }
            return new LuaIdentifierNameSyntax(name);
        }

        public override LuaSyntaxNode VisitQualifiedName(QualifiedNameSyntax node) {
            return new LuaIdentifierNameSyntax(node.ToString());
        }

        public override LuaSyntaxNode VisitArgumentList(ArgumentListSyntax node) {
            LuaArgumentListSyntax argumentList = new LuaArgumentListSyntax();
            foreach(var argument in node.Arguments) {
                var newNode = (LuaArgumentSyntax)argument.Accept(this);
                argumentList.Arguments.Add(newNode);
            }
            return argumentList;
        }

        public override LuaSyntaxNode VisitArgument(ArgumentSyntax node) {
            LuaExpressionSyntax expression = (LuaExpressionSyntax)node.Expression.Accept(this);
            LuaArgumentSyntax argument = new LuaArgumentSyntax(expression);
            return argument;
        }

        public override LuaSyntaxNode VisitLiteralExpression(LiteralExpressionSyntax node) {
            switch(node.Kind()) {
                case SyntaxKind.CharacterLiteralExpression: {
                        return new LuaCharacterLiteralExpression((char)node.Token.Value);
                    }
                case SyntaxKind.NullLiteralExpression: {
                        return new LuaIdentifierLiteralExpressionSyntax(LuaIdentifierNameSyntax.Nil);
                    }
                default: {
                        return new LuaIdentifierLiteralExpressionSyntax(node.Token.Text);
                    }
            }
        }

        public override LuaSyntaxNode VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node) {
            var declaration = (LuaVariableDeclarationSyntax)node.Declaration.Accept(this);
            return new LuaLocalDeclarationStatementSyntax(declaration);
        }

        public override LuaSyntaxNode VisitVariableDeclaration(VariableDeclarationSyntax node) {
            LuaVariableListDeclarationSyntax variableListDeclaration = new LuaVariableListDeclarationSyntax();
            foreach(VariableDeclaratorSyntax variable in node.Variables) {
                var variableDeclarator = (LuaVariableDeclaratorSyntax)variable.Accept(this);
                variableListDeclaration.Variables.Add(variableDeclarator);
            }
            bool isMultiNil = variableListDeclaration.Variables.Count > 0 && variableListDeclaration.Variables.All(i => i.Initializer == null);
            if(isMultiNil) {
                LuaLocalVariablesStatementSyntax declarationStatement = new LuaLocalVariablesStatementSyntax();
                foreach(var variable in variableListDeclaration.Variables) {
                    declarationStatement.Variables.Add(variable.Identifier);
                }
                return declarationStatement;
            }
            else {
                return variableListDeclaration;
            }
        }

        public override LuaSyntaxNode VisitVariableDeclarator(VariableDeclaratorSyntax node) {
            LuaIdentifierNameSyntax identifier = new LuaIdentifierNameSyntax(node.Identifier.ValueText);
            LuaVariableDeclaratorSyntax variableDeclarator = new LuaVariableDeclaratorSyntax(identifier);
            if(node.Initializer != null) {
                variableDeclarator.Initializer = (LuaEqualsValueClauseSyntax)node.Initializer.Accept(this);
            }
            return variableDeclarator;
        }

        public override LuaSyntaxNode VisitEqualsValueClause(EqualsValueClauseSyntax node) {
            var expression = (LuaExpressionSyntax)node.Value.Accept(this);
            return new LuaEqualsValueClauseSyntax(expression);
        }

        public override LuaSyntaxNode VisitPredefinedType(PredefinedTypeSyntax node) {
            ISymbol symbol = semanticModel_.GetSymbolInfo(node).Symbol;
            string typeName = xmlMetaProvider_.GetTypeMapName(symbol);
            return new LuaIdentifierNameSyntax(typeName);
        }

        private void WriteStatementOrBlock(StatementSyntax statement, LuaBlockSyntax luablock) {
            if(statement.IsKind(SyntaxKind.Block)) {
                var blockNode = (LuaBlockSyntax)statement.Accept(this);
                luablock.Statements.AddRange(blockNode.Statements);
            }
            else {
                blocks_.Push(luablock);
                var statementNode = (LuaStatementSyntax)statement.Accept(this);
                luablock.Statements.Add(statementNode);
                blocks_.Pop();
            }
        }

        #region if else switch

        public override LuaSyntaxNode VisitIfStatement(IfStatementSyntax node) {
            var condition = (LuaExpressionSyntax)node.Condition.Accept(this);
            LuaIfStatementSyntax ifStatement = new LuaIfStatementSyntax(condition);
            WriteStatementOrBlock(node.Statement, ifStatement.Body);
            ifStatement.Else = (LuaElseClauseSyntax)node.Else?.Accept(this);
            return ifStatement;
        }

        public override LuaSyntaxNode VisitElseClause(ElseClauseSyntax node) {
            LuaStatementSyntax statement;
            if(node.Statement.IsKind(SyntaxKind.IfStatement)) {
                statement = (LuaStatementSyntax)node.Statement.Accept(this);
            }
            else {
                LuaBlockSyntax block = new LuaBlockSyntax();
                WriteStatementOrBlock(node.Statement, block);
                statement = block;
            }
            LuaElseClauseSyntax elseClause = new LuaElseClauseSyntax(statement);
            return elseClause;
        }

        public override LuaSyntaxNode VisitSwitchStatement(SwitchStatementSyntax node) {
            var temp = GetTempIdentifier(node);
            LuaSwitchAdapterStatementSyntax switchStatement = new LuaSwitchAdapterStatementSyntax(temp);
            switchs_.Push(switchStatement);
            var expression = (LuaExpressionSyntax)node.Expression.Accept(this);
            switchStatement.Fill(expression, node.Sections.Select(i => (LuaStatementSyntax)i.Accept(this)));
            switchs_.Pop();
            return switchStatement;
        }

        public override LuaSyntaxNode VisitSwitchSection(SwitchSectionSyntax node) {
            bool isDefault = node.Labels.Any(i => i.Kind() == SyntaxKind.DefaultSwitchLabel);
            if(isDefault) {
                LuaBlockSyntax block = new LuaBlockSyntax();
                foreach(var statement in node.Statements) {
                    var luaStatement = (LuaStatementSyntax)statement.Accept(this);
                    block.Statements.Add(luaStatement);
                }
                return block;
            }
            else {
                var expressions = node.Labels.Select(i => (LuaExpressionSyntax)i.Accept(this));
                var condition = expressions.Aggregate((x, y) => new LuaBinaryExpressionSyntax(x, LuaSyntaxNode.Tokens.Or, y));
                LuaIfStatementSyntax ifStatement = new LuaIfStatementSyntax(condition);
                foreach(var statement in node.Statements) {
                    var luaStatement = (LuaStatementSyntax)statement.Accept(this);
                    ifStatement.Body.Statements.Add(luaStatement);
                }
                return ifStatement;
            }
        }

        public override LuaSyntaxNode VisitCaseSwitchLabel(CaseSwitchLabelSyntax node) {
            var left = switchs_.Peek().Temp;
            var right = (LuaLiteralExpressionSyntax)node.Value.Accept(this);
            LuaBinaryExpressionSyntax BinaryExpression = new LuaBinaryExpressionSyntax(left, LuaSyntaxNode.Tokens.EqualsEquals, right);
            return BinaryExpression;
        }

        #endregion

        public override LuaSyntaxNode VisitBreakStatement(BreakStatementSyntax node) {
            return new LuaBreakStatementSyntax();
        }

        public override LuaSyntaxNode VisitBinaryExpression(BinaryExpressionSyntax node) {
            var left = (LuaExpressionSyntax)node.Left.Accept(this);
            var right = (LuaExpressionSyntax)node.Right.Accept(this);
            string operatorToken = GetOperatorToken(node.OperatorToken.ValueText);
            return new LuaBinaryExpressionSyntax(left, operatorToken, right);
        }

        private LuaAssignmentExpressionSyntax GetLuaAssignmentExpressionSyntax(ExpressionSyntax operand, bool isPlus) {
            var expression = (LuaExpressionSyntax)operand.Accept(this);
            string operatorToken = isPlus ? LuaSyntaxNode.Tokens.Plus : LuaSyntaxNode.Tokens.Sub;
            LuaBinaryExpressionSyntax binary = new LuaBinaryExpressionSyntax(expression, operatorToken, LuaIdentifierNameSyntax.One);
            LuaAssignmentExpressionSyntax assignment = new LuaAssignmentExpressionSyntax(expression, binary);
            return assignment;
        }

        public override LuaSyntaxNode VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node) {
            SyntaxKind kind = node.Kind();
            if(kind == SyntaxKind.PreIncrementExpression || kind == SyntaxKind.PreDecrementExpression) {
                LuaAssignmentExpressionSyntax assignment = GetLuaAssignmentExpressionSyntax(node.Operand, kind == SyntaxKind.PreIncrementExpression);
                if(node.Parent.IsKind(SyntaxKind.ExpressionStatement)) {
                    return assignment;
                }
                else {
                    CurBlock.Statements.Add(new LuaExpressionStatementSyntax(assignment));
                    return assignment.Left;
                }
            }
            else {
                var operand = (LuaExpressionSyntax)node.Operand.Accept(this);
                string operatorToken = GetOperatorToken(node.OperatorToken.ValueText);
                LuaPrefixUnaryExpressionSyntax unaryExpression = new LuaPrefixUnaryExpressionSyntax(operand, operatorToken);
                return unaryExpression;
            }
        }

        public override LuaSyntaxNode VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node) {
            SyntaxKind kind = node.Kind();
            if(kind != SyntaxKind.PostIncrementExpression && kind != SyntaxKind.PostDecrementExpression) {
                throw new NotSupportedException();
            }
            LuaAssignmentExpressionSyntax assignment = GetLuaAssignmentExpressionSyntax(node.Operand, kind == SyntaxKind.PostIncrementExpression);
            if(node.Parent.IsKind(SyntaxKind.ExpressionStatement)) {
                return assignment;
            }
            else {
                var temp = GetTempIdentifier(node);
                LuaVariableDeclaratorSyntax variableDeclarator = new LuaVariableDeclaratorSyntax(temp);
                variableDeclarator.Initializer = new LuaEqualsValueClauseSyntax(assignment.Left);
                CurBlock.Statements.Add(new LuaLocalVariableDeclaratorSyntax(variableDeclarator));
                CurBlock.Statements.Add(new LuaExpressionStatementSyntax(assignment));
                return temp;
            }
        }

        public override LuaSyntaxNode VisitThrowStatement(ThrowStatementSyntax node) {
            LuaInvocationExpressionSyntax invocationExpression = new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.Throw);
            var expression = (LuaExpressionSyntax)node.Expression.Accept(this);
            invocationExpression.AddArgument(expression);
            return new LuaExpressionStatementSyntax(invocationExpression);
        }

        public override LuaSyntaxNode VisitForEachStatement(ForEachStatementSyntax node) {
            LuaIdentifierNameSyntax identifier = new LuaIdentifierNameSyntax(node.Identifier.ValueText);
            var expression = (LuaExpressionSyntax)node.Expression.Accept(this);
            LuaForInStatementSyntax forInStatement = new LuaForInStatementSyntax(identifier, expression);
            WriteStatementOrBlock(node.Statement, forInStatement.Body);
            return forInStatement;
        }

        public override LuaSyntaxNode VisitWhileStatement(WhileStatementSyntax node) {
            var condition = (LuaExpressionSyntax)node.Condition.Accept(this);
            LuaWhileStatementSyntax whileStatement = new LuaWhileStatementSyntax(condition);
            WriteStatementOrBlock(node.Statement, whileStatement.Body);
            return whileStatement;
        }

        public override LuaSyntaxNode VisitForStatement(ForStatementSyntax node) {
            LuaBlockSyntax body = new LuaBlockSyntax();
            blocks_.Push(body);

            if(node.Declaration != null) {
                body.Statements.Add((LuaVariableDeclarationSyntax)node.Declaration.Accept(this));
            }
            var initializers = node.Initializers.Select(i => new LuaExpressionStatementSyntax((LuaExpressionSyntax)i.Accept(this)));
            body.Statements.AddRange(initializers);

            LuaExpressionSyntax condition = node.Condition != null ? (LuaExpressionSyntax)node.Condition.Accept(this) : LuaIdentifierNameSyntax.True;
            LuaWhileStatementSyntax whileStatement = new LuaWhileStatementSyntax(condition);
            blocks_.Push(whileStatement.Body);
            WriteStatementOrBlock(node.Statement, whileStatement.Body);
            var incrementors = node.Incrementors.Select(i => new LuaExpressionStatementSyntax((LuaExpressionSyntax)i.Accept(this)));
            whileStatement.Body.Statements.AddRange(incrementors);
            blocks_.Pop();
            body.Statements.Add(whileStatement);
            blocks_.Pop();

            return new LuaBlockBlockSyntax(body);
        }

        public override LuaSyntaxNode VisitDoStatement(DoStatementSyntax node) {
            var condition = (LuaExpressionSyntax)node.Condition.Accept(this);
            var newCondition = new LuaPrefixUnaryExpressionSyntax(new LuaParenthesizedExpressionSyntax(condition), LuaSyntaxNode.Keyword.Not);
            LuaRepeatStatementSyntax repeatStatement = new LuaRepeatStatementSyntax(newCondition);
            WriteStatementOrBlock(node.Statement, repeatStatement.Body);
            return repeatStatement;
        }

        public override LuaSyntaxNode VisitYieldStatement(YieldStatementSyntax node) {
            CurFunction.HasYield = true;
            var expression = (LuaExpressionSyntax)node.Expression.Accept(this);
            if(node.IsKind(SyntaxKind.YieldBreakStatement)) {
                LuaReturnStatementSyntax returnStatement = new LuaReturnStatementSyntax(expression);
                return returnStatement;
            }
            else {
                LuaInvocationExpressionSyntax invocationExpression = new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.YieldReturn);
                invocationExpression.AddArgument(expression);
                return new LuaExpressionStatementSyntax(invocationExpression);
            }
        }

        public override LuaSyntaxNode VisitParenthesizedExpression(ParenthesizedExpressionSyntax node) {
            var expression = (LuaExpressionSyntax)node.Expression.Accept(this);
            return new LuaParenthesizedExpressionSyntax(expression);
        }

        /// <summary>
        /// http://lua-users.org/wiki/TernaryOperator
        /// </summary>
        public override LuaSyntaxNode VisitConditionalExpression(ConditionalExpressionSyntax node) {
            var type = semanticModel_.GetTypeInfo(node.WhenTrue).Type;
            bool mayBeNullOrFalse;
            if(type.IsValueType) {
                mayBeNullOrFalse = type.ToString() == "bool";
            }
            else {
                mayBeNullOrFalse = true;
            }

            if(mayBeNullOrFalse) {
                var temp = GetTempIdentifier(node);
                var condition = (LuaExpressionSyntax)node.Condition.Accept(this);
                LuaIfStatementSyntax ifStatement = new LuaIfStatementSyntax(condition);
                blocks_.Push(ifStatement.Body);
                var whenTrue = (LuaExpressionSyntax)node.WhenTrue.Accept(this);
                blocks_.Pop();
                ifStatement.Body.Statements.Add(new LuaExpressionStatementSyntax(new LuaAssignmentExpressionSyntax(temp, whenTrue)));

                LuaBlockSyntax block = new LuaBlockSyntax();
                blocks_.Push(block);
                var whenFalse = (LuaExpressionSyntax)node.WhenFalse.Accept(this);
                blocks_.Pop();
                block.Statements.Add(new LuaExpressionStatementSyntax(new LuaAssignmentExpressionSyntax(temp, whenFalse)));

                ifStatement.Else = new LuaElseClauseSyntax(block);
                CurBlock.Statements.Add(new LuaLocalVariableDeclaratorSyntax(new LuaVariableDeclaratorSyntax(temp)));
                CurBlock.Statements.Add(ifStatement);
                return temp;
            }
            else {
                var condition = (LuaExpressionSyntax)node.Condition.Accept(this);
                var whenTrue = (LuaExpressionSyntax)node.WhenTrue.Accept(this);
                var whenFalse = (LuaExpressionSyntax)node.WhenFalse.Accept(this);
                return new LuaBinaryExpressionSyntax(new LuaBinaryExpressionSyntax(condition, LuaSyntaxNode.Tokens.And, whenTrue), LuaSyntaxNode.Tokens.Or, whenFalse);
            }
        }

        public override LuaSyntaxNode VisitGotoStatement(GotoStatementSyntax node) {
            if(node.CaseOrDefaultKeyword.IsKind(SyntaxKind.None)) {
                var identifier = (LuaIdentifierNameSyntax)node.Expression.Accept(this);
                return new LuaGotoStatement(identifier);
            }
            else if(node.CaseOrDefaultKeyword.IsKind(SyntaxKind.CaseKeyword)) {
                var label = (LuaLiteralExpressionSyntax)node.Expression.Accept(this);
                var temp = new LuaIdentifierNameSyntax("label_" + label.Text);
                switchs_.Peek().AddCaseLabel(temp, label.Text);
                return new LuaGotoCaseAdapterStatement(temp);
            }
            else {
                var temp = new LuaIdentifierNameSyntax("label_default");
                switchs_.Peek().AddDefaultLabel(temp);
                return new LuaGotoCaseAdapterStatement(temp);
            }
        }

        public override LuaSyntaxNode VisitLabeledStatement(LabeledStatementSyntax node) {
            LuaIdentifierNameSyntax identifier = new LuaIdentifierNameSyntax(node.Identifier.ValueText);
            var statement = (LuaStatementSyntax)node.Statement.Accept(this);
            return new LuaLabeledStatement(identifier, statement);
        }
    }
}