package rewriter

import (
	"fmt"
	"go/ast"
	"go/importer"
	"go/parser"
	"go/token"
	"go/types"
	"io/fs"
	"os"
	"path/filepath"
	"strings"
)

type moduleModel struct {
	source     string
	modulePath string
	fset       *token.FileSet
	packages   []*packageModel
}

type packageModel struct {
	directory string
	path      string
	name      string
	files     []*sourceFile
	typesPkg  *types.Package
	info      *types.Info
	decls     map[*types.Func]*ast.FuncDecl
}

type sourceFile struct {
	path        string
	rel         string
	attribution string
	file        *ast.File
	pkg         *packageModel
}

func analyzeModule(source, modulePath string) (*moduleModel, error) {
	model := &moduleModel{source: source, modulePath: modulePath, fset: token.NewFileSet()}
	packages := make(map[string]*packageModel)
	attributionPrefix := repositoryRelativePrefix(source)
	err := filepath.WalkDir(source, func(path string, entry fs.DirEntry, walkErr error) error {
		if walkErr != nil {
			return walkErr
		}
		if entry.IsDir() {
			if (entry.Name() == "vendor" || isVCSDirectory(entry.Name())) && path != source {
				return filepath.SkipDir
			}
			return nil
		}
		if filepath.Ext(path) != ".go" {
			return nil
		}
		parsed, err := parser.ParseFile(model.fset, path, nil, parser.ParseComments)
		if err != nil {
			return fmt.Errorf("parse %s: %w", path, err)
		}
		directory := filepath.Dir(path)
		key := directory + "\x00" + parsed.Name.Name
		pkg := packages[key]
		if pkg == nil {
			relDirectory, err := filepath.Rel(source, directory)
			if err != nil {
				return err
			}
			packagePath := modulePath
			if relDirectory != "." {
				packagePath += "/" + filepath.ToSlash(relDirectory)
			}
			pkg = &packageModel{directory: directory, path: packagePath, name: parsed.Name.Name}
			packages[key] = pkg
			model.packages = append(model.packages, pkg)
		}
		rel, err := filepath.Rel(source, path)
		if err != nil {
			return err
		}
		attribution := rel
		if attributionPrefix != "" {
			attribution = filepath.Join(attributionPrefix, rel)
		}
		file := &sourceFile{
			path: path, rel: filepath.ToSlash(rel), attribution: filepath.ToSlash(attribution),
			file: parsed, pkg: pkg,
		}
		pkg.files = append(pkg.files, file)
		return nil
	})
	if err != nil {
		return nil, fmt.Errorf("scan source module: %w", err)
	}
	for _, pkg := range model.packages {
		if err := typeCheckPackage(model.fset, pkg); err != nil {
			return nil, err
		}
	}
	return model, nil
}

func repositoryRelativePrefix(source string) string {
	repositoryRoot := strings.TrimSpace(os.Getenv("BEHAVIORDIFF_REPOSITORY_ROOT"))
	if repositoryRoot == "" {
		return ""
	}
	absoluteRoot, err := filepath.Abs(repositoryRoot)
	if err != nil {
		return ""
	}
	relative, err := filepath.Rel(filepath.Clean(absoluteRoot), filepath.Clean(source))
	if err != nil || relative == "." || relative == ".." || strings.HasPrefix(relative, ".."+string(filepath.Separator)) {
		return ""
	}
	return relative
}

func typeCheckPackage(fset *token.FileSet, pkg *packageModel) error {
	files := make([]*ast.File, 0, len(pkg.files))
	for _, file := range pkg.files {
		files = append(files, file.file)
	}
	info := &types.Info{
		Types:      make(map[ast.Expr]types.TypeAndValue),
		Defs:       make(map[*ast.Ident]types.Object),
		Uses:       make(map[*ast.Ident]types.Object),
		Selections: make(map[*ast.SelectorExpr]*types.Selection),
	}
	config := &types.Config{Importer: importer.Default(), Error: func(error) {}}
	typesPkg, _ := config.Check(pkg.path, fset, files, info)
	if typesPkg == nil {
		return fmt.Errorf("type-check %s did not produce a package", pkg.path)
	}
	pkg.typesPkg = typesPkg
	pkg.info = info
	pkg.decls = make(map[*types.Func]*ast.FuncDecl)
	for _, file := range pkg.files {
		for _, declaration := range file.file.Decls {
			function, ok := declaration.(*ast.FuncDecl)
			if !ok || function.Body == nil {
				continue
			}
			if object, ok := info.Defs[function.Name].(*types.Func); ok {
				pkg.decls[object] = function
			}
		}
	}
	return nil
}
