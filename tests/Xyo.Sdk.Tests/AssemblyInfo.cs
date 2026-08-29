using Xunit;

// A handful of tests (XyoClientConfigTests) mutate the process-wide XYO_API_BASE_URL environment
// variable. xUnit runs different test classes in parallel by default, which raced that mutation
// against unrelated tests constructing XyoClientConfig concurrently. The suite is small (well under
// a second either way), so disabling collection parallelization is the correct trade-off over
// building an environment-variable injection seam just for two tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
