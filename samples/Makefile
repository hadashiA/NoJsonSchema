# Sample code generation (run from samples/).
# The CLI is multi-targeted, so `dotnet run` needs an explicit -f; override with `make CLI_FW=net9.0`.

ROOT    := ..
CLI_FW  ?= net10.0
CLI     := dotnet run --project $(ROOT)/src/NoJsonSchema.Cli -c Release -f $(CLI_FW) --

.DEFAULT_GOAL := help

.PHONY: help gen gen-bench gen-dap build test bench

help: ## List available targets
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | sort | awk 'BEGIN{FS=":.*?## "}{printf "  \033[36m%-12s\033[0m %s\n", $$1, $$2}'

gen: gen-bench gen-dap ## Regenerate all sample code from their schemas

gen-bench: ## Regenerate Bench/Generated
	$(CLI) generate -i Bench/bench-schema.json -o Bench/Generated -n NoJsonBench

gen-dap: ## Regenerate Dap/Generated
	$(CLI) generate -i Dap/debugAdapterProtocol.json -o Dap/Generated -n Dap

build: ## Build the whole solution (Release)
	dotnet build -c Release $(ROOT)/NoJsonSchema.slnx

test: ## Run the full test suite
	dotnet test $(ROOT)/NoJsonSchema.slnx

bench: ## Run the benchmarks (pass FILTER=... to narrow, e.g. make bench FILTER='*DeserializeLarge*')
	dotnet run --project Bench -c Release -- --filter '$(or $(FILTER),*)'
