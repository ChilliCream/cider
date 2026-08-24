using System.Runtime.CompilerServices;

// Exposes the internal parsing helpers in this folder (e.g. LaunchdInstaller.ParseLaunchctlPrint)
// to the unit tests without making them part of the public API surface in CONTRACTS.md §G.
[assembly: InternalsVisibleTo("Cider.Tests")]
