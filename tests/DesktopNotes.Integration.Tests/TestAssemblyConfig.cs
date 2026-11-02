using Xunit;

// Disable test parallelization across test classes because integration tests share
// the static AppLocalizer singleton and SQLite test environments.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
