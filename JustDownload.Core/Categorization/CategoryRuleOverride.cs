namespace JustDownload.Core.Categorization;

/// <summary>
/// A single user customisation of the categorisation rules (TASK-085): the (<see cref="Scope"/>,
/// <see cref="Key"/>) pair should resolve to <see cref="Category"/>. These deltas are persisted over the
/// seeded defaults so a user's tweaks survive restarts; the defaults themselves stay in code.
/// </summary>
/// <param name="Scope">Which rule dimension the override targets.</param>
/// <param name="Key">The extension, MIME type, or MIME top-level the override keys on.</param>
/// <param name="Category">The category the key resolves to.</param>
public sealed record CategoryRuleOverride(CategoryRuleScope Scope, string Key, FileCategory Category);
