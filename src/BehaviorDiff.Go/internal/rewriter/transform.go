package rewriter

import (
	"bytes"
	"fmt"
	"go/ast"
	"go/format"
	"go/parser"
	"go/token"
	"go/types"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

type transformer struct {
	model   *moduleModel
	out     string
	runtime string
	report  *Report
}

type functionTransformer struct {
	owner     *transformer
	file      *sourceFile
	alias     string
	usedNames map[string]bool
	frameName string
}

type resolution struct {
	declaration *ast.FuncDecl
	kind        string
}

type memberDefinition struct {
	Module           string
	Method           string
	File             string
	Line             int
	ReturnKind       string
	SourceResolution string
	Status           string
	SkipReason       string
	Detail           string
	IsTestRoot       bool
	IsHarness        bool
}

func transformModule(model *moduleModel, out, runtimeImport string, report *Report) error {
	transformer := &transformer{model: model, out: out, runtime: runtimeImport, report: report}
	report.Metrics.Packages = len(model.packages)
	for _, pkg := range model.packages {
		if err := transformer.validateCompanionNames(pkg); err != nil {
			return err
		}
		for _, file := range pkg.files {
			if err := transformer.transformFile(file); err != nil {
				return err
			}
		}
	}
	return nil
}

func (transformer *transformer) validateCompanionNames(pkg *packageModel) error {
	functions := make(map[string]bool)
	methods := make(map[string]bool)
	for _, file := range pkg.files {
		for _, declaration := range file.file.Decls {
			function, ok := declaration.(*ast.FuncDecl)
			if !ok {
				continue
			}
			if function.Recv == nil {
				functions[function.Name.Name] = true
			} else {
				methods[receiverTypeName(function.Recv)+"."+function.Name.Name] = true
			}
		}
	}
	for _, file := range pkg.files {
		for _, declaration := range file.file.Decls {
			function, ok := declaration.(*ast.FuncDecl)
			if !ok || function.Body == nil {
				continue
			}
			companion := companionName(function.Name.Name)
			if function.Recv == nil && functions[companion] {
				return fmt.Errorf("companion name collision in %s: %s", pkg.path, companion)
			}
			key := receiverTypeName(function.Recv) + "." + companion
			if function.Recv != nil && methods[key] {
				return fmt.Errorf("companion method name collision in %s: %s", pkg.path, key)
			}
		}
	}
	return nil
}

func (transformer *transformer) transformFile(file *sourceFile) error {
	boundaryStart := len(transformer.report.Boundaries)
	originals := make([]*ast.FuncDecl, 0)
	for _, declaration := range file.file.Decls {
		if function, ok := declaration.(*ast.FuncDecl); ok && function.Body != nil {
			originals = append(originals, function)
		}
	}
	if len(originals) == 0 {
		return nil
	}
	alias := runtimeAlias(file.file)
	addImport(file.file, alias, transformer.runtime)
	newDeclarations := make([]ast.Decl, 0, len(file.file.Decls)+len(originals))
	for _, declaration := range file.file.Decls {
		newDeclarations = append(newDeclarations, declaration)
		function, ok := declaration.(*ast.FuncDecl)
		if !ok || function.Body == nil {
			continue
		}
		companion, err := transformer.transformFunction(file, function, alias)
		if err != nil {
			return err
		}
		newDeclarations = append(newDeclarations, companion)
	}
	newDeclarations = append(newDeclarations, registrationDeclaration(
		file,
		alias,
		originals,
		transformer.report.Boundaries[boundaryStart:],
		transformer.model.fset,
	))
	file.file.Decls = newDeclarations
	var output bytes.Buffer
	if err := format.Node(&output, transformer.model.fset, file.file); err != nil {
		return fmt.Errorf("format %s: %w", file.rel, err)
	}
	destination := filepath.Join(transformer.out, filepath.FromSlash(file.rel))
	if err := os.WriteFile(destination, output.Bytes(), 0o644); err != nil {
		return fmt.Errorf("write %s: %w", destination, err)
	}
	transformer.report.Metrics.Files++
	return nil
}

func (transformer *transformer) transformFunction(file *sourceFile, wrapper *ast.FuncDecl, alias string) (*ast.FuncDecl, error) {
	companionType, err := cloneFuncType(wrapper.Type)
	if err != nil {
		return nil, fmt.Errorf("clone signature for %s: %w", wrapper.Name.Name, err)
	}
	companionReceiver, err := cloneFieldList(wrapper.Recv)
	if err != nil {
		return nil, fmt.Errorf("clone receiver for %s: %w", wrapper.Name.Name, err)
	}
	companion := &ast.FuncDecl{
		Recv: companionReceiver,
		Name: ast.NewIdent(companionName(wrapper.Name.Name)),
		Type: companionType,
		Body: wrapper.Body,
	}
	usedNames := collectIdentifiers(companion)
	parentName := uniqueName("__bd_parent", usedNames)
	frameName := uniqueName("__bd_frame", usedNames)
	prependFrameParameter(companion.Type, alias, parentName)
	resultNames := nameResults(companion.Type, usedNames)
	ensureParameterNames(wrapper.Type.Params, companion.Type.Params, usedNames)
	receiverName := ensureReceiverName(wrapper.Recv, companion.Recv, usedNames)
	metadata := memberMetadata(transformer.model.fset, file, wrapper, isTestRoot(file, wrapper))
	activeFrame := frameName
	if metadata.IsTestRoot {
		activeFrame = parentName
	}
	functionTransformer := &functionTransformer{
		owner: transformer, file: file, alias: alias, usedNames: usedNames, frameName: activeFrame,
	}
	companion.Body = functionTransformer.rewriteBlock(companion.Body, activeFrame)
	if metadata.IsTestRoot {
		wrapper.Body = testWrapperBody(wrapper, companion.Name.Name, alias, metadata, usedNames)
	} else {
		companion.Body.List = append([]ast.Stmt{
			enterStatement(alias, frameName, parentName, metadataExpression(alias, metadata), argumentExpression(companion.Type.Params, receiverName)),
			exitStatement(alias, frameName, resultNames, usedNames),
		}, companion.Body.List...)
		wrapper.Body = wrapperBody(wrapper, companion.Name.Name, receiverName)
	}
	transformer.report.Metrics.Companions++
	transformer.report.Metrics.Patched++
	if metadata.IsTestRoot {
		transformer.report.Metrics.TestRoots++
	}
	if wrapper.Recv == nil {
		transformer.report.Metrics.Functions++
	} else {
		transformer.report.Metrics.Methods++
	}
	return companion, nil
}

func (functionTransformer *functionTransformer) resolve(call *ast.CallExpr) resolution {
	base := callTargetBase(call.Fun)
	info := functionTransformer.file.pkg.info
	switch target := base.(type) {
	case *ast.Ident:
		object := info.Uses[target]
		if function, ok := object.(*types.Func); ok {
			if declaration := functionTransformer.file.pkg.decls[function]; declaration != nil {
				return resolution{declaration: declaration}
			}
			return resolution{}
		}
		switch object.(type) {
		case *types.Builtin, *types.TypeName:
			return resolution{}
		}
	case *ast.SelectorExpr:
		if selection := info.Selections[target]; selection != nil {
			if selection.Kind() == types.MethodExpr {
				return resolution{kind: "method-expression-call"}
			}
			if isInterface(selection.Recv()) {
				return resolution{kind: "interface-call"}
			}
			if function, ok := selection.Obj().(*types.Func); ok {
				if declaration := functionTransformer.file.pkg.decls[function]; declaration != nil {
					return resolution{declaration: declaration}
				}
				if declaration := functionTransformer.file.pkg.decls[function.Origin()]; declaration != nil {
					return resolution{declaration: declaration}
				}
				return resolution{}
			}
		} else if function, ok := info.Uses[target.Sel].(*types.Func); ok && function.Pkg() != nil {
			if isModulePackage(functionTransformer.owner.model.modulePath, function.Pkg().Path()) {
				return resolution{kind: "cross-package-call"}
			}
			return resolution{}
		} else if importedPath := importedPackagePath(functionTransformer.file.file, target); isModulePackage(functionTransformer.owner.model.modulePath, importedPath) {
			return resolution{kind: "cross-package-call"}
		}
	}
	if callType := info.TypeOf(call.Fun); callType != nil {
		if _, ok := callType.Underlying().(*types.Signature); ok {
			if _, literal := call.Fun.(*ast.FuncLit); !literal {
				return resolution{kind: "function-value-call"}
			}
		}
	}
	return resolution{}
}

func importedPackagePath(file *ast.File, selectorExpression *ast.SelectorExpr) string {
	packageName, ok := selectorExpression.X.(*ast.Ident)
	if !ok {
		return ""
	}
	for _, importSpecification := range file.Imports {
		importPath, err := strconv.Unquote(importSpecification.Path.Value)
		if err != nil {
			continue
		}
		alias := filepath.Base(importPath)
		if importSpecification.Name != nil {
			alias = importSpecification.Name.Name
		}
		if alias == packageName.Name {
			return importPath
		}
	}
	return ""
}

func isModulePackage(modulePath, packagePath string) bool {
	return packagePath == modulePath || strings.HasPrefix(packagePath, modulePath+"/")
}

func (functionTransformer *functionTransformer) addBoundary(call *ast.CallExpr, kind string) {
	position := functionTransformer.owner.model.fset.Position(call.Pos())
	var expression bytes.Buffer
	_ = format.Node(&expression, functionTransformer.owner.model.fset, call)
	functionTransformer.owner.report.Boundaries = append(functionTransformer.owner.report.Boundaries, Boundary{
		File: functionTransformer.file.rel, Line: position.Line, Column: position.Column,
		Kind: kind, Expression: expression.String(),
	})
	functionTransformer.owner.report.Metrics.Skipped++
}

func cloneFuncType(functionType *ast.FuncType) (*ast.FuncType, error) {
	var source bytes.Buffer
	if err := format.Node(&source, token.NewFileSet(), functionType); err != nil {
		return nil, err
	}
	declarationSource := "package clone\nfunc cloned" + strings.TrimPrefix(source.String(), "func") + " {}"
	file, err := parser.ParseFile(token.NewFileSet(), "clone.go", declarationSource, 0)
	if err != nil {
		return nil, err
	}
	cloned, ok := file.Decls[0].(*ast.FuncDecl)
	if !ok {
		return nil, fmt.Errorf("cloned declaration is %T", file.Decls[0])
	}
	return cloned.Type, nil
}

func cloneFieldList(fields *ast.FieldList) (*ast.FieldList, error) {
	if fields == nil {
		return nil, nil
	}
	cloned := &ast.FieldList{List: make([]*ast.Field, 0, len(fields.List))}
	for _, field := range fields.List {
		var source bytes.Buffer
		if err := format.Node(&source, token.NewFileSet(), field.Type); err != nil {
			return nil, err
		}
		typeExpression, err := parser.ParseExpr(source.String())
		if err != nil {
			return nil, err
		}
		copyField := &ast.Field{Type: typeExpression, Tag: field.Tag}
		for _, name := range field.Names {
			copyField.Names = append(copyField.Names, ast.NewIdent(name.Name))
		}
		cloned.List = append(cloned.List, copyField)
	}
	return cloned, nil
}

func collectIdentifiers(node ast.Node) map[string]bool {
	used := make(map[string]bool)
	ast.Inspect(node, func(node ast.Node) bool {
		if identifier, ok := node.(*ast.Ident); ok {
			used[identifier.Name] = true
		}
		return true
	})
	return used
}

func uniqueName(base string, used map[string]bool) string {
	if !used[base] {
		used[base] = true
		return base
	}
	for suffix := 1; ; suffix++ {
		candidate := base + strconv.Itoa(suffix)
		if !used[candidate] {
			used[candidate] = true
			return candidate
		}
	}
}

func prependFrameParameter(functionType *ast.FuncType, alias, name string) {
	if functionType.Params == nil {
		functionType.Params = &ast.FieldList{}
	}
	functionType.Params.List = append([]*ast.Field{{
		Names: []*ast.Ident{ast.NewIdent(name)}, Type: framePointer(alias),
	}}, functionType.Params.List...)
}

func nameResults(functionType *ast.FuncType, used map[string]bool) []string {
	if functionType.Results == nil {
		return nil
	}
	resultNames := make([]string, 0)
	resultIndex := 0
	for _, field := range functionType.Results.List {
		if len(field.Names) == 0 {
			name := uniqueName(fmt.Sprintf("__bd_result%d", resultIndex), used)
			field.Names = []*ast.Ident{ast.NewIdent(name)}
			resultNames = append(resultNames, name)
			resultIndex++
			continue
		}
		for _, name := range field.Names {
			resultNames = append(resultNames, name.Name)
			resultIndex++
		}
	}
	return resultNames
}

func ensureParameterNames(wrapper, companion *ast.FieldList, used map[string]bool) {
	if wrapper == nil || companion == nil {
		return
	}
	companionOffset := len(companion.List) - len(wrapper.List)
	argumentIndex := 0
	for index, field := range wrapper.List {
		companionField := companion.List[index+companionOffset]
		if len(field.Names) == 0 {
			name := uniqueName(fmt.Sprintf("__bd_arg%d", argumentIndex), used)
			field.Names = []*ast.Ident{ast.NewIdent(name)}
			companionField.Names = []*ast.Ident{ast.NewIdent(name)}
			argumentIndex++
			continue
		}
		for nameIndex, name := range field.Names {
			if name.Name == "_" {
				generated := uniqueName(fmt.Sprintf("__bd_arg%d", argumentIndex), used)
				name.Name = generated
				companionField.Names[nameIndex].Name = generated
			}
			argumentIndex++
		}
	}
}

func ensureReceiverName(wrapper, companion *ast.FieldList, used map[string]bool) string {
	if wrapper == nil || len(wrapper.List) == 0 {
		return ""
	}
	field := wrapper.List[0]
	companionField := companion.List[0]
	if len(field.Names) == 0 || field.Names[0].Name == "_" {
		name := uniqueName("__bd_receiver", used)
		field.Names = []*ast.Ident{ast.NewIdent(name)}
		companionField.Names = []*ast.Ident{ast.NewIdent(name)}
		return name
	}
	return field.Names[0].Name
}

func wrapperBody(wrapper *ast.FuncDecl, companion, receiver string) *ast.BlockStmt {
	var target ast.Expr = ast.NewIdent(companion)
	if wrapper.Recv != nil {
		target = &ast.SelectorExpr{X: ast.NewIdent(receiver), Sel: ast.NewIdent(companion)}
	}
	arguments := []ast.Expr{ast.NewIdent("nil")}
	if wrapper.Type.Params != nil {
		for _, field := range wrapper.Type.Params.List {
			for _, name := range field.Names {
				arguments = append(arguments, ast.NewIdent(name.Name))
			}
		}
	}
	call := &ast.CallExpr{Fun: target, Args: arguments}
	if wrapper.Type.Params != nil && len(wrapper.Type.Params.List) > 0 {
		if _, variadic := wrapper.Type.Params.List[len(wrapper.Type.Params.List)-1].Type.(*ast.Ellipsis); variadic {
			call.Ellipsis = token.Pos(1)
		}
	}
	if wrapper.Type.Results == nil || len(wrapper.Type.Results.List) == 0 {
		return &ast.BlockStmt{List: []ast.Stmt{&ast.ExprStmt{X: call}}}
	}
	return &ast.BlockStmt{List: []ast.Stmt{&ast.ReturnStmt{Results: []ast.Expr{call}}}}
}

func enterStatement(alias, frame, parent string, metadata, arguments ast.Expr) ast.Stmt {
	return &ast.AssignStmt{Lhs: []ast.Expr{ast.NewIdent(frame)}, Tok: token.DEFINE, Rhs: []ast.Expr{&ast.CallExpr{
		Fun: selector(alias, "Enter"), Args: []ast.Expr{
			ast.NewIdent(parent), metadata, arguments,
		},
	}}}
}

func exitStatement(alias, frame string, results []string, usedNames map[string]bool) ast.Stmt {
	var captured ast.Expr = ast.NewIdent("nil")
	if len(results) > 0 {
		elements := make([]ast.Expr, 0, len(results))
		for _, result := range results {
			elements = append(elements, &ast.UnaryExpr{Op: token.AND, X: ast.NewIdent(result)})
		}
		captured = &ast.CompositeLit{Type: &ast.ArrayType{Elt: ast.NewIdent("any")}, Elts: elements}
	}
	recovered := uniqueName("__bd_recovered", usedNames)
	return &ast.DeferStmt{Call: &ast.CallExpr{Fun: &ast.FuncLit{
		Type: &ast.FuncType{Params: &ast.FieldList{}},
		Body: &ast.BlockStmt{List: []ast.Stmt{
			&ast.AssignStmt{Lhs: []ast.Expr{ast.NewIdent(recovered)}, Tok: token.DEFINE, Rhs: []ast.Expr{&ast.CallExpr{Fun: ast.NewIdent("recover")}}},
			&ast.ExprStmt{X: &ast.CallExpr{Fun: selector(alias, "Exit"), Args: []ast.Expr{ast.NewIdent(frame), captured, ast.NewIdent(recovered)}}},
			&ast.IfStmt{Cond: &ast.BinaryExpr{X: ast.NewIdent(recovered), Op: token.NEQ, Y: ast.NewIdent("nil")}, Body: &ast.BlockStmt{List: []ast.Stmt{
				&ast.ExprStmt{X: &ast.CallExpr{Fun: ast.NewIdent("panic"), Args: []ast.Expr{ast.NewIdent(recovered)}}},
			}}},
		}},
	}}}
}

func rewriteCallTarget(call *ast.CallExpr, companion, frame string) {
	call.Fun = replaceTargetName(call.Fun, companion)
	call.Args = append([]ast.Expr{ast.NewIdent(frame)}, call.Args...)
}

func replaceTargetName(expression ast.Expr, name string) ast.Expr {
	switch expression := expression.(type) {
	case *ast.Ident:
		return ast.NewIdent(name)
	case *ast.SelectorExpr:
		expression.Sel = ast.NewIdent(name)
		return expression
	case *ast.IndexExpr:
		expression.X = replaceTargetName(expression.X, name)
		return expression
	case *ast.IndexListExpr:
		expression.X = replaceTargetName(expression.X, name)
		return expression
	default:
		return expression
	}
}

func callTargetBase(expression ast.Expr) ast.Expr {
	switch expression := expression.(type) {
	case *ast.IndexExpr:
		return callTargetBase(expression.X)
	case *ast.IndexListExpr:
		return callTargetBase(expression.X)
	case *ast.ParenExpr:
		return callTargetBase(expression.X)
	default:
		return expression
	}
}

func runtimeAlias(file *ast.File) string {
	used := collectIdentifiers(file)
	return uniqueName("behaviordiffrt", used)
}

func addImport(file *ast.File, alias, importPath string) {
	specification := &ast.ImportSpec{Name: ast.NewIdent(alias), Path: &ast.BasicLit{
		Kind: token.STRING, Value: strconv.Quote(importPath),
	}}
	for _, declaration := range file.Decls {
		general, ok := declaration.(*ast.GenDecl)
		if ok && general.Tok == token.IMPORT {
			if general.Lparen == token.NoPos {
				general.Lparen = token.Pos(1)
			}
			general.Specs = append(general.Specs, specification)
			file.Imports = append(file.Imports, specification)
			return
		}
	}
	file.Decls = append([]ast.Decl{&ast.GenDecl{Tok: token.IMPORT, Specs: []ast.Spec{specification}}}, file.Decls...)
	file.Imports = append(file.Imports, specification)
}

func framePointer(alias string) ast.Expr {
	return &ast.StarExpr{X: selector(alias, "Frame")}
}

func selector(alias, name string) *ast.SelectorExpr {
	return &ast.SelectorExpr{X: ast.NewIdent(alias), Sel: ast.NewIdent(name)}
}

func companionName(name string) string {
	return "__bd_" + name
}

func methodName(pkg *packageModel, function *ast.FuncDecl) string {
	signature := "()"
	if object, ok := pkg.info.Defs[function.Name].(*types.Func); ok {
		if typed, ok := object.Type().(*types.Signature); ok {
			signature = stableSignature(typed)
		}
	}
	if function.Recv == nil {
		return pkg.path + "." + function.Name.Name + signature
	}
	return pkg.path + "." + receiverTypeName(function.Recv) + "." + function.Name.Name + signature
}

func stableSignature(signature *types.Signature) string {
	var rendered strings.Builder
	rendered.WriteByte('(')
	for index := 0; index < signature.Params().Len(); index++ {
		if index > 0 {
			rendered.WriteByte(',')
		}
		parameterType := signature.Params().At(index).Type()
		if signature.Variadic() && index == signature.Params().Len()-1 {
			slice := parameterType.(*types.Slice)
			rendered.WriteString("...")
			rendered.WriteString(types.TypeString(slice.Elem(), packageQualifier))
		} else {
			rendered.WriteString(types.TypeString(parameterType, packageQualifier))
		}
	}
	rendered.WriteByte(')')
	if signature.Results().Len() == 1 {
		rendered.WriteByte(' ')
		rendered.WriteString(types.TypeString(signature.Results().At(0).Type(), packageQualifier))
	} else if signature.Results().Len() > 1 {
		rendered.WriteString(" (")
		for index := 0; index < signature.Results().Len(); index++ {
			if index > 0 {
				rendered.WriteByte(',')
			}
			rendered.WriteString(types.TypeString(signature.Results().At(index).Type(), packageQualifier))
		}
		rendered.WriteByte(')')
	}
	return rendered.String()
}

func packageQualifier(pkg *types.Package) string {
	return pkg.Path()
}

func memberMetadata(fset *token.FileSet, file *sourceFile, function *ast.FuncDecl, testRoot bool) memberDefinition {
	resolved := fset.Position(function.Name.Pos())
	returnKind := "Void"
	if function.Type.Results != nil && len(function.Type.Results.List) > 0 {
		returnKind = "Sync"
	}
	return memberDefinition{
		Module: file.pkg.path, Method: methodName(file.pkg, function), File: file.attribution,
		Line: resolved.Line, ReturnKind: returnKind, SourceResolution: "debugInfo",
		Status: "Patched", IsTestRoot: testRoot, IsHarness: testRoot,
	}
}

func isTestRoot(file *sourceFile, function *ast.FuncDecl) bool {
	if function.Recv != nil || !strings.HasPrefix(function.Name.Name, "Test") || function.Type.Params == nil || len(function.Type.Params.List) != 1 {
		return false
	}
	parameter := function.Type.Params.List[0]
	pointer, ok := parameter.Type.(*ast.StarExpr)
	if !ok {
		return false
	}
	selectorExpression, ok := pointer.X.(*ast.SelectorExpr)
	return ok && selectorExpression.Sel.Name == "T" && importedPackagePath(file.file, selectorExpression) == "testing"
}

func metadataExpression(alias string, member memberDefinition) ast.Expr {
	fields := []ast.Expr{
		metadataField("Module", stringLiteral(member.Module)),
		metadataField("Method", stringLiteral(member.Method)),
		metadataField("File", stringLiteral(member.File)),
		metadataField("Line", &ast.BasicLit{Kind: token.INT, Value: strconv.Itoa(member.Line)}),
		metadataField("ReturnKind", stringLiteral(member.ReturnKind)),
		metadataField("SourceResolution", stringLiteral(member.SourceResolution)),
		metadataField("Status", stringLiteral(member.Status)),
	}
	if member.SkipReason != "" {
		fields = append(fields, metadataField("SkipReason", stringLiteral(member.SkipReason)))
	}
	if member.Detail != "" {
		fields = append(fields, metadataField("Detail", stringLiteral(member.Detail)))
	}
	if member.IsTestRoot {
		fields = append(fields, metadataField("IsTestRoot", ast.NewIdent("true")))
	}
	if member.IsHarness {
		fields = append(fields, metadataField("IsHarness", ast.NewIdent("true")))
	}
	return &ast.CompositeLit{Type: selector(alias, "Member"), Elts: fields}
}

func metadataField(name string, value ast.Expr) ast.Expr {
	return &ast.KeyValueExpr{Key: ast.NewIdent(name), Value: value}
}

func stringLiteral(value string) ast.Expr {
	return &ast.BasicLit{Kind: token.STRING, Value: strconv.Quote(value)}
}

func argumentExpression(parameters *ast.FieldList, receiver string) ast.Expr {
	elements := make([]ast.Expr, 0)
	if receiver != "" {
		elements = append(elements, ast.NewIdent(receiver))
	}
	if parameters != nil {
		for index, field := range parameters.List {
			if index == 0 {
				continue
			}
			for _, name := range field.Names {
				elements = append(elements, ast.NewIdent(name.Name))
			}
		}
	}
	return &ast.CompositeLit{Type: &ast.ArrayType{Elt: ast.NewIdent("any")}, Elts: elements}
}

func wrapperArguments(wrapper *ast.FuncDecl) []ast.Expr {
	arguments := make([]ast.Expr, 0)
	if wrapper.Type.Params != nil {
		for _, field := range wrapper.Type.Params.List {
			for _, name := range field.Names {
				arguments = append(arguments, ast.NewIdent(name.Name))
			}
		}
	}
	return arguments
}

func testWrapperBody(wrapper *ast.FuncDecl, companion, alias string, metadata memberDefinition, usedNames map[string]bool) *ast.BlockStmt {
	root := uniqueName("__bd_root", usedNames)
	recovered := uniqueName("__bd_recovered", usedNames)
	arguments := wrapperArguments(wrapper)
	open := &ast.AssignStmt{Lhs: []ast.Expr{ast.NewIdent(root)}, Tok: token.DEFINE, Rhs: []ast.Expr{&ast.CallExpr{
		Fun: selector(alias, "OpenTest"), Args: []ast.Expr{
			&ast.CallExpr{Fun: &ast.SelectorExpr{X: arguments[0], Sel: ast.NewIdent("Name")}},
			metadataExpression(alias, metadata),
			&ast.CompositeLit{Type: &ast.ArrayType{Elt: ast.NewIdent("any")}, Elts: arguments},
		},
	}}}
	exit := &ast.DeferStmt{Call: &ast.CallExpr{Fun: &ast.FuncLit{Type: &ast.FuncType{Params: &ast.FieldList{}}, Body: &ast.BlockStmt{List: []ast.Stmt{
		&ast.AssignStmt{Lhs: []ast.Expr{ast.NewIdent(recovered)}, Tok: token.DEFINE, Rhs: []ast.Expr{&ast.CallExpr{Fun: ast.NewIdent("recover")}}},
		&ast.ExprStmt{X: &ast.CallExpr{Fun: selector(alias, "Exit"), Args: []ast.Expr{ast.NewIdent(root), ast.NewIdent("nil"), ast.NewIdent(recovered)}}},
		&ast.IfStmt{Cond: &ast.BinaryExpr{X: ast.NewIdent(recovered), Op: token.NEQ, Y: ast.NewIdent("nil")}, Body: &ast.BlockStmt{List: []ast.Stmt{
			&ast.ExprStmt{X: &ast.CallExpr{Fun: ast.NewIdent("panic"), Args: []ast.Expr{ast.NewIdent(recovered)}}},
		}}},
	}}}}}
	callArguments := append([]ast.Expr{ast.NewIdent(root)}, arguments...)
	call := &ast.ExprStmt{X: &ast.CallExpr{Fun: ast.NewIdent(companion), Args: callArguments}}
	return &ast.BlockStmt{List: []ast.Stmt{open, exit, call}}
}

func registrationDeclaration(file *sourceFile, alias string, functions []*ast.FuncDecl, boundaries []Boundary, fset *token.FileSet) ast.Decl {
	members := make([]ast.Expr, 0, len(functions)+len(boundaries))
	for _, function := range functions {
		members = append(members, metadataExpression(alias, memberMetadata(fset, file, function, isTestRoot(file, function))))
	}
	for _, boundary := range boundaries {
		skipReason := "UnsupportedShape"
		if boundary.Kind == "cross-package-call" {
			skipReason = "DeclaredExternally"
		}
		members = append(members, metadataExpression(alias, memberDefinition{
			Module: file.pkg.path,
			Method: fmt.Sprintf("%s.<boundary:%s@%s:%d:%d>", file.pkg.path, boundary.Kind, boundary.File, boundary.Line, boundary.Column),
			File:   boundary.File, Line: boundary.Line, ReturnKind: "Boundary", SourceResolution: "debugInfo",
			Status: "Skipped", SkipReason: skipReason, Detail: "Go: " + boundary.Kind,
		}))
	}
	return &ast.GenDecl{Tok: token.VAR, Specs: []ast.Spec{&ast.ValueSpec{
		Names:  []*ast.Ident{ast.NewIdent("_")},
		Values: []ast.Expr{&ast.CallExpr{Fun: selector(alias, "Register"), Args: members}},
	}}}
}

func receiverTypeName(receiver *ast.FieldList) string {
	if receiver == nil || len(receiver.List) == 0 {
		return ""
	}
	expression := receiver.List[0].Type
	if star, ok := expression.(*ast.StarExpr); ok {
		expression = star.X
	}
	if index, ok := expression.(*ast.IndexExpr); ok {
		expression = index.X
	}
	if index, ok := expression.(*ast.IndexListExpr); ok {
		expression = index.X
	}
	if identifier, ok := expression.(*ast.Ident); ok {
		return identifier.Name
	}
	return "receiver"
}

func isInterface(value types.Type) bool {
	if pointer, ok := value.(*types.Pointer); ok {
		value = pointer.Elem()
	}
	_, ok := value.Underlying().(*types.Interface)
	return ok
}
