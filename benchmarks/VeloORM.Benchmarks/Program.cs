using BenchmarkDotNet.Running;
using VeloORM.Benchmarks;

// Run all benchmark classes, or filter: `-- --filter *Flat*` / `*Relational*`.
BenchmarkSwitcher.FromAssembly(typeof(FlatBenchmarks).Assembly).Run(args);
