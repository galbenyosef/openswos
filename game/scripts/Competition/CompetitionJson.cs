using System.Text.Json.Serialization;

namespace OpenSwos.Competition;

// ============================================================================
// Source-generated JSON for the career save.
//
// WHY. A career save is the WHOLE world: 1730 clubs and about 29 000 players,
// written after every fixture. The user reported the client freezing; measuring
// it (2026-08-24) found the freeze was almost entirely this save. Serializing
// the SAME career state three ways:
//
//   indented, reflection   38.4 MB   253 ms   <- what shipped before
//   compact,  reflection   17.3 MB   236 ms
//   compact,  generated    17.3 MB   165 ms   <- now
//
// and writing the bytes to disk costs 19 ms either way, so the disk was never
// the problem. End to end that took VIEW RESULT from ~250 ms of frozen client
// down to ~90 ms, and the season rollover from 2.5 s to 1.7 s.
//
// System.Text.Json's source generator emits a writer per type at build time
// instead of walking the object graph with reflection at run time. Nothing
// about the FORMAT changes: the two compact outputs above are byte-identical,
// and every existing save (indented ones included) still loads.
//
// If a model type is ever added that the generator cannot handle, the store
// falls back to the reflection path on its own (see CompetitionStore) — a save
// must never be lost to a serializer detail.
// ============================================================================

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(CompetitionState))]
public partial class CompetitionJsonContext : JsonSerializerContext
{
}
