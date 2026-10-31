using Xunit;

// Several tests change the process-wide ImagingDefaults; run test classes one at a time so they can't interfere.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
