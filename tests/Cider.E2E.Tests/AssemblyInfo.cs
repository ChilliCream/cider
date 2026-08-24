using Xunit;

// Every test drives one shared daemon and one shared Apple container runtime: running classes in
// parallel would fight over ports, container names and VM capacity.
[assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]
