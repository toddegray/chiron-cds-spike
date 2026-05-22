#!/usr/bin/env bash
# Project manifest is the .NET solution file (chiron-cds-spike.sln); the
# prod-gates harness doesn't recognize .sln as a manifest, so we declare
# the coverage command here.

set -euo pipefail

# Run the engine + integration test suites with coverage collection via Coverlet.
export COVERAGE_CMD="dotnet test chiron-cds-spike.sln --collect:'XPlat Code Coverage'"

# Lint / type-check equivalent: dotnet build treats warnings as errors per
# Directory.Build.props, so build success implies clean compile + nullable + xmldoc.
export LINT_CMD="dotnet build chiron-cds-spike.sln --nologo --verbosity:quiet"
