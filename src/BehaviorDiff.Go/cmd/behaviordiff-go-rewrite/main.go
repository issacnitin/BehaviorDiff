package main

import (
	"flag"
	"fmt"
	"os"

	"github.com/behaviordiff/behaviordiff-go/internal/rewriter"
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
		"GO_REWRITE_SUMMARY methods=%d companions=%d direct=%d go=%d boundaries=%d report=%s\n",
		report.Metrics.Methods,
		report.Metrics.Companions,
		report.Metrics.DirectCalls,
		report.Metrics.GoStatements,
		report.Metrics.Boundaries,
		rewriter.ReportFileName,
	)
}
