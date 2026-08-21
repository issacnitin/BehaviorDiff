package rewriter

import (
	"bufio"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

func readModulePath(path string) (string, error) {
	file, err := os.Open(path)
	if err != nil {
		return "", fmt.Errorf("open go.mod: %w", err)
	}
	defer file.Close()
	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		fields := strings.Fields(line)
		if len(fields) != 2 || fields[0] != "module" {
			continue
		}
		modulePath := fields[1]
		if unquoted, err := strconv.Unquote(modulePath); err == nil {
			modulePath = unquoted
		}
		if modulePath != "" {
			return modulePath, nil
		}
	}
	if err := scanner.Err(); err != nil {
		return "", fmt.Errorf("read go.mod: %w", err)
	}
	return "", fmt.Errorf("go.mod does not declare a module")
}

func copyModuleTree(source, out string) error {
	if err := os.MkdirAll(out, 0o755); err != nil {
		return fmt.Errorf("create output: %w", err)
	}
	return filepath.WalkDir(source, func(path string, entry fs.DirEntry, walkErr error) error {
		if walkErr != nil {
			return walkErr
		}
		rel, err := filepath.Rel(source, path)
		if err != nil {
			return err
		}
		if entry.IsDir() && rel != "." && isVCSDirectory(entry.Name()) {
			return filepath.SkipDir
		}
		destination := filepath.Join(out, rel)
		if entry.IsDir() {
			return os.MkdirAll(destination, 0o755)
		}
		info, err := entry.Info()
		if err != nil {
			return err
		}
		if !info.Mode().IsRegular() {
			return fmt.Errorf("unsupported non-regular source entry: %s", path)
		}
		return copyFile(path, destination, info.Mode().Perm())
	})
}

func copyFile(source, destination string, mode fs.FileMode) error {
	input, err := os.Open(source)
	if err != nil {
		return fmt.Errorf("open %s: %w", source, err)
	}
	defer input.Close()
	output, err := os.OpenFile(destination, os.O_CREATE|os.O_EXCL|os.O_WRONLY, mode)
	if err != nil {
		return fmt.Errorf("create %s: %w", destination, err)
	}
	if _, err := io.Copy(output, input); err != nil {
		_ = output.Close()
		return fmt.Errorf("copy %s: %w", source, err)
	}
	if err := output.Close(); err != nil {
		return fmt.Errorf("close %s: %w", destination, err)
	}
	return nil
}

func isVCSDirectory(name string) bool {
	switch name {
	case ".git", ".hg", ".svn":
		return true
	default:
		return false
	}
}
