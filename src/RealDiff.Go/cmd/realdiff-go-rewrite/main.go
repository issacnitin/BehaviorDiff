package main

import (
	"flag"
	"fmt"
	"os"

	"github.com/realdiff/realdiff-go/internal/rewriter"
)

func main() {
	source := flag.String("source", "", "source Go module")
	out := flag.String("out", "", "output cache directory")
	flag.Parse()

	if *source == "" || *out == "" {
		fmt.Fprintln(os.Stderr, "both --source and --out are required")
		os.Exit(2)
	}

	report, err := rewriter.Rewrite(rewriter.Options{Source: *source, Out: *out})
	if err != nil {
		fmt.Fprintf(os.Stderr, "rewrite failed: %v\n", err)
		os.Exit(1)
	}

	fmt.Printf(
		"GO_REWRITE_SUMMARY methods=%d companions=%d roots=%d patched=%d skipped=%d templates=%d direct=%d go=%d boundaries=%d report=%s\n",
		report.Metrics.Methods,
		report.Metrics.Companions,
		report.Metrics.TestRoots,
		report.Metrics.Patched,
		report.Metrics.Skipped,
		report.Metrics.GenericTemplates,
		report.Metrics.DirectCalls,
		report.Metrics.GoStatements,
		report.Metrics.Boundaries,
		rewriter.ReportFileName,
	)
}
