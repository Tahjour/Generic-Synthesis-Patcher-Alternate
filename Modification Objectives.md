# Generic Deep Record Handling, All-Field Forwarding, and Mutagen Upgrade

## Summary

Modernize GSP’s record-processing engine so every writable Mutagen field can be forwarded, nested collections can be merged precisely, HPU works across all supported value shapes, and Skyrim records accept Mutagen, existing GSP, UESP, and signature-based names.

Upgrade to Mutagen 0.54.4, Synthesis 0.36.6, and .NET 9—the lowest framework supported by those latest stable packages.

## Public Configuration Behavior

- Add `RecordTypes` as a case-insensitive synonym for `Types`. If both occur, union and deduplicate them.
- In default mod-indexed forwarding, an empty field array means all eligible fields:

```json
{
  "RecordTypes": "QUST",
  "Forward": {
    "QuestPatch.esp": []
  }
}
```

- Expand that empty array into every top-level serialized, writable field exposed for the selected record type. Exclude identity, registration, computed, and Mutagen bookkeeping properties; forwarding a complex top-level field includes its complete contents.
- Preserve explicit arrays such as `"Plugin.esp": ["Type", "Aliases"]`.
- Process multiple mod-indexed entries in JSON declaration order, so later entries may overwrite fields forwarded by earlier entries.
- Restrict the new all-field shorthand to default mod-indexed forwarding. In field-indexed forwarding, `"Field": []` continues to mean all eligible source plugins.
- Make all-field forwarding behave exactly like listing every eligible field individually, including per-field `OnlyIfDefault`, logging, change counts, and error handling.
- Add `ForwardOptions.Merge`. It implies `IndexedByField`, operates only on list leaves, and merges the winner with the sources selected for that field; an empty source list means all enabled overrides.
- Support implicit collection traversal such as `"Aliases.Conditions"`:
  - `"Aliases"` replaces the entire alias collection.
  - `"Aliases.Conditions"` correlates aliases by alias ID and modifies only Conditions.
  - Missing correlated parents are deep-cloned from the source before applying the child operation.
- HPU supports scalars, enums, flags, nullable form links, complex objects, and ordered whole-list values. Nested HPU is evaluated separately for each correlated parent.
- Reject incompatible option combinations during preflight, including `Merge` with HPU, Random, SelfMasterOnly, or DefaultThenSelfMasterOnly.

## Implementation Changes

- Replace repeated dotted-name reflection with cached property-path descriptors containing canonical names, aliases, collection boundaries, identities, getters/setters, copy strategy, and supported actions.
- Add generic adapters for scalar and nullable values, enums, flags, translated strings, form links, complex Mutagen objects, and arbitrary collections. This makes Quest `Type` and other currently fill-only enums forwardable.
- Normalize form links to `FormKey` for equality and HPU, then write through the correct mutable form-link type. This directly fixes the supplied ActivationSound, WaterType, and InteractionKeyword failures.
- Correlate nested collections by stable identity: Quest aliases by alias ID, form-link entries by FormKey, indexed entries by ID/index, primitives by value, and otherwise by documented ordinal fallback.
- Generalize the ancestry-aware record graph for arbitrary list leaves while preserving winner order, deterministic additions, plugin inclusion/exclusion, and prior GSP changes already present in the patch record.
- Generate the all-field expansion from the same descriptor registry used for explicit paths, ensuring new Mutagen fields automatically become eligible when safe.
- Add a checked-in Skyrim alias catalog scoped by owning record/subrecord type. Accept case- and separator-insensitive selectors such as `ANIO`, `AnimationObject`, or `AnimatedObject`, and `MODL` or `Model`.
- Retain existing behavior for Fill, Copy, explicit Forward lists, top-level Merge, filters, groups, and Fallout 4/Oblivion naming.
- Upgrade all Mutagen game packages to 0.54.4, Synthesis to 0.36.6, Mutagen testing to 0.54.4, and both projects to .NET 9. Complete API migrations surfaced by compilation, including the replacement of `NoOwner` with `UntypedOwner`.

## Validation, Errors, and Documentation

- Preflight every expanded rule/type/action/path after group inheritance and before scanning records. Aggregate all failures and abort configuration loading without creating patch records.
- Diagnostics include config filename, group/rule ID, supplied and canonical record type/path, failing segment, owning CLR type, requested action/options, reason, compatible actions, and close-name suggestions.
- Treat unknown types, absent fields, ambiguous UESP signatures, read-only/computed fields, invalid deep traversal, duplicate parent identities, and incompatible options explicitly.
- Document the two distinct empty-array meanings prominently:
  - `"Plugin.esp": []` → all fields in default mod-indexed forwarding.
  - `"Field": []` → all enabled source plugins in field-indexed forwarding.
- Update schemas, generated field data, config documentation, and examples with deep Quest merging, all-field forwarding, HPU lists, `RecordTypes`, and UESP aliases.

## Test Plan

- Preserve the original 573-test baseline and add regression coverage for the dependency/API migration.
- Verify all-field forwarding against explicit enumeration, including complex fields, lists, nulls, `OnlyIfDefault`, multiple plugin keys, declaration order, and excluded infrastructure fields.
- Validate legacy configs and every JSON file in the supplied configuration ZIP unchanged.
- Test `Types`/`RecordTypes`, ANIO naming variants, MODL/Model, case/separator normalization, ambiguous aliases, and close-name diagnostics.
- Build synthetic Skyrim override chains for Quest Type forwarding/HPU and Alias-to-Conditions replacement, Merge, HPU, missing-parent cloning, additions, removals, and deterministic ordering.
- Reproduce the supplied nullable-form-link HPU cases and verify no invalid `FormKey` conversion errors.
- Confirm invalid configurations produce one aggregated preflight report and no patch output.
- Regenerate and validate all schemas/field documentation, then run Debug and Release builds and the complete test suite under .NET 9.

## Assumptions and Current Workspace State

- “All fields” means serialized, writable record data recognized by the descriptor registry, not FormKey, registration data, timestamps, or computed/runtime properties.
- Comprehensive UESP alias coverage is delivered for Skyrim first; the deep engine remains game-neutral.
- The workspace currently contains provisional .NET/package-version edits and four `NoOwner`-to-`UntypedOwner` migrations from the compatibility probe. Implementation should review and complete those edits; no feature implementation has otherwise been started.
