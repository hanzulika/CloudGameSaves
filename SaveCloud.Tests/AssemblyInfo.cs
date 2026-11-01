using Xunit;

// AppPaths is process-wide static state; run test classes one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
