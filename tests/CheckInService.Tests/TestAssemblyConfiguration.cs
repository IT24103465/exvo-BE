using Xunit;

// Tests share an in-memory database, so run test collections sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]