package rewriter

import (
	"go/ast"
	"go/token"
	"strings"
)

func (functionTransformer *functionTransformer) rewriteBlock(block *ast.BlockStmt, frameName string) *ast.BlockStmt {
	if block == nil {
		return nil
	}
	for index, statement := range block.List {
		block.List[index] = functionTransformer.rewriteStatement(statement, frameName)
	}
	return block
}

func (functionTransformer *functionTransformer) rewriteStatement(statement ast.Stmt, frameName string) ast.Stmt {
	switch statement := statement.(type) {
	case *ast.BlockStmt:
		return functionTransformer.rewriteBlock(statement, frameName)
	case *ast.ExprStmt:
		statement.X = functionTransformer.rewriteExpression(statement.X, frameName)
	case *ast.AssignStmt:
		for index, expression := range statement.Lhs {
			statement.Lhs[index] = functionTransformer.rewriteExpression(expression, frameName)
		}
		for index, expression := range statement.Rhs {
			statement.Rhs[index] = functionTransformer.rewriteExpression(expression, frameName)
		}
	case *ast.ReturnStmt:
		for index, expression := range statement.Results {
			statement.Results[index] = functionTransformer.rewriteExpression(expression, frameName)
		}
	case *ast.DeclStmt:
		functionTransformer.rewriteDeclaration(statement.Decl, frameName)
	case *ast.GoStmt:
		return functionTransformer.rewriteGo(statement, frameName)
	case *ast.DeferStmt:
		statement.Call = functionTransformer.rewriteExpression(statement.Call, frameName).(*ast.CallExpr)
	case *ast.IfStmt:
		if statement.Init != nil {
			statement.Init = functionTransformer.rewriteStatement(statement.Init, frameName)
		}
		statement.Cond = functionTransformer.rewriteExpression(statement.Cond, frameName)
		statement.Body = functionTransformer.rewriteBlock(statement.Body, frameName)
		if statement.Else != nil {
			statement.Else = functionTransformer.rewriteStatement(statement.Else, frameName)
		}
	case *ast.ForStmt:
		if statement.Init != nil {
			statement.Init = functionTransformer.rewriteStatement(statement.Init, frameName)
		}
		if statement.Cond != nil {
			statement.Cond = functionTransformer.rewriteExpression(statement.Cond, frameName)
		}
		if statement.Post != nil {
			statement.Post = functionTransformer.rewriteStatement(statement.Post, frameName)
		}
		statement.Body = functionTransformer.rewriteBlock(statement.Body, frameName)
	case *ast.RangeStmt:
		if statement.Key != nil {
			statement.Key = functionTransformer.rewriteExpression(statement.Key, frameName)
		}
		if statement.Value != nil {
			statement.Value = functionTransformer.rewriteExpression(statement.Value, frameName)
		}
		statement.X = functionTransformer.rewriteExpression(statement.X, frameName)
		statement.Body = functionTransformer.rewriteBlock(statement.Body, frameName)
	case *ast.SwitchStmt:
		if statement.Init != nil {
			statement.Init = functionTransformer.rewriteStatement(statement.Init, frameName)
		}
		if statement.Tag != nil {
			statement.Tag = functionTransformer.rewriteExpression(statement.Tag, frameName)
		}
		statement.Body = functionTransformer.rewriteBlock(statement.Body, frameName)
	case *ast.TypeSwitchStmt:
		if statement.Init != nil {
			statement.Init = functionTransformer.rewriteStatement(statement.Init, frameName)
		}
		statement.Assign = functionTransformer.rewriteStatement(statement.Assign, frameName)
		statement.Body = functionTransformer.rewriteBlock(statement.Body, frameName)
	case *ast.SelectStmt:
		statement.Body = functionTransformer.rewriteBlock(statement.Body, frameName)
	case *ast.SendStmt:
		statement.Chan = functionTransformer.rewriteExpression(statement.Chan, frameName)
		statement.Value = functionTransformer.rewriteExpression(statement.Value, frameName)
	case *ast.IncDecStmt:
		statement.X = functionTransformer.rewriteExpression(statement.X, frameName)
	case *ast.LabeledStmt:
		statement.Stmt = functionTransformer.rewriteStatement(statement.Stmt, frameName)
	case *ast.CaseClause:
		for index, expression := range statement.List {
			statement.List[index] = functionTransformer.rewriteExpression(expression, frameName)
		}
		for index, child := range statement.Body {
			statement.Body[index] = functionTransformer.rewriteStatement(child, frameName)
		}
	case *ast.CommClause:
		if statement.Comm != nil {
			statement.Comm = functionTransformer.rewriteStatement(statement.Comm, frameName)
		}
		for index, child := range statement.Body {
			statement.Body[index] = functionTransformer.rewriteStatement(child, frameName)
		}
	}
	return statement
}

func (functionTransformer *functionTransformer) rewriteDeclaration(declaration ast.Decl, frameName string) {
	general, ok := declaration.(*ast.GenDecl)
	if !ok {
		return
	}
	for _, specification := range general.Specs {
		value, ok := specification.(*ast.ValueSpec)
		if !ok {
			continue
		}
		for index, expression := range value.Values {
			value.Values[index] = functionTransformer.rewriteExpression(expression, frameName)
		}
	}
}

func (functionTransformer *functionTransformer) rewriteExpression(expression ast.Expr, frameName string) ast.Expr {
	switch expression := expression.(type) {
	case *ast.CallExpr:
		for index, argument := range expression.Args {
			expression.Args[index] = functionTransformer.rewriteExpression(argument, frameName)
		}
		if target, ok := expression.Fun.(*ast.FuncLit); ok {
			target.Body = functionTransformer.rewriteBlock(target.Body, frameName)
		} else {
			expression.Fun = functionTransformer.rewriteExpression(expression.Fun, frameName)
		}
		resolved := functionTransformer.resolve(expression)
		if resolved.declaration != nil {
			rewriteCallTarget(expression, companionName(resolved.declaration.Name.Name), frameName)
			functionTransformer.owner.report.Metrics.DirectCalls++
		} else if resolved.kind != "" {
			functionTransformer.addBoundary(expression, resolved.kind)
		}
	case *ast.FuncLit:
		expression.Body = functionTransformer.rewriteBlock(expression.Body, frameName)
	case *ast.BinaryExpr:
		expression.X = functionTransformer.rewriteExpression(expression.X, frameName)
		expression.Y = functionTransformer.rewriteExpression(expression.Y, frameName)
	case *ast.UnaryExpr:
		expression.X = functionTransformer.rewriteExpression(expression.X, frameName)
	case *ast.ParenExpr:
		expression.X = functionTransformer.rewriteExpression(expression.X, frameName)
	case *ast.SelectorExpr:
		expression.X = functionTransformer.rewriteExpression(expression.X, frameName)
	case *ast.IndexExpr:
		expression.X = functionTransformer.rewriteExpression(expression.X, frameName)
		expression.Index = functionTransformer.rewriteExpression(expression.Index, frameName)
	case *ast.IndexListExpr:
		expression.X = functionTransformer.rewriteExpression(expression.X, frameName)
		for index, item := range expression.Indices {
			expression.Indices[index] = functionTransformer.rewriteExpression(item, frameName)
		}
	case *ast.SliceExpr:
		expression.X = functionTransformer.rewriteExpression(expression.X, frameName)
		if expression.Low != nil {
			expression.Low = functionTransformer.rewriteExpression(expression.Low, frameName)
		}
		if expression.High != nil {
			expression.High = functionTransformer.rewriteExpression(expression.High, frameName)
		}
		if expression.Max != nil {
			expression.Max = functionTransformer.rewriteExpression(expression.Max, frameName)
		}
	case *ast.TypeAssertExpr:
		expression.X = functionTransformer.rewriteExpression(expression.X, frameName)
	case *ast.StarExpr:
		expression.X = functionTransformer.rewriteExpression(expression.X, frameName)
	case *ast.KeyValueExpr:
		expression.Key = functionTransformer.rewriteExpression(expression.Key, frameName)
		expression.Value = functionTransformer.rewriteExpression(expression.Value, frameName)
	case *ast.CompositeLit:
		for index, item := range expression.Elts {
			expression.Elts[index] = functionTransformer.rewriteExpression(item, frameName)
		}
	}
	return expression
}

func (functionTransformer *functionTransformer) rewriteGo(statement *ast.GoStmt, frameName string) ast.Stmt {
	call := statement.Call
	childName := uniqueName("__bd_child", functionTransformer.usedNames)
	parentStatements := make([]ast.Stmt, 0, len(call.Args)+2)
	argumentStart := 0
	var childCall *ast.CallExpr
	if literal, ok := call.Fun.(*ast.FuncLit); ok {
		literal.Body = functionTransformer.rewriteBlock(literal.Body, childName)
		childCall = call
	} else {
		resolved := functionTransformer.resolve(call)
		if resolved.declaration == nil {
			kind := resolved.kind
			if kind == "" {
				kind = "dynamic-go"
			} else {
				kind = strings.TrimSuffix(kind, "-call") + "-go"
			}
			functionTransformer.addBoundary(call, kind)
			call.Fun = functionTransformer.rewriteExpression(call.Fun, frameName)
			return statement
		}
		call.Fun = functionTransformer.rewriteExpression(call.Fun, frameName)
		rewriteCallTarget(call, companionName(resolved.declaration.Name.Name), childName)
		argumentStart = 1
		if _, method := callTargetBase(call.Fun).(*ast.SelectorExpr); method {
			targetName := uniqueName("__bd_go_target", functionTransformer.usedNames)
			parentStatements = append(parentStatements, &ast.AssignStmt{
				Lhs: []ast.Expr{ast.NewIdent(targetName)}, Tok: token.DEFINE, Rhs: []ast.Expr{call.Fun},
			})
			call.Fun = ast.NewIdent(targetName)
		}
		childCall = call
	}
	for index := argumentStart; index < len(call.Args); index++ {
		argument := call.Args[index]
		argument = functionTransformer.rewriteExpression(argument, frameName)
		argumentName := uniqueName("__bd_go_arg", functionTransformer.usedNames)
		parentStatements = append(parentStatements, &ast.AssignStmt{
			Lhs: []ast.Expr{ast.NewIdent(argumentName)}, Tok: token.DEFINE, Rhs: []ast.Expr{argument},
		})
		call.Args[index] = ast.NewIdent(argumentName)
	}
	functionTransformer.owner.report.Metrics.GoStatements++
	closure := &ast.FuncLit{
		Type: &ast.FuncType{Params: &ast.FieldList{List: []*ast.Field{{
			Names: []*ast.Ident{ast.NewIdent(childName)}, Type: framePointer(functionTransformer.alias),
		}}}},
		Body: &ast.BlockStmt{List: []ast.Stmt{&ast.ExprStmt{X: childCall}}},
	}
	parentStatements = append(parentStatements, &ast.ExprStmt{X: &ast.CallExpr{
		Fun: selector(functionTransformer.alias, "Go"), Args: []ast.Expr{ast.NewIdent(frameName), closure},
	}})
	return &ast.BlockStmt{List: parentStatements}
}
