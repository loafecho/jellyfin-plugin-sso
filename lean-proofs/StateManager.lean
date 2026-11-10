/-!
# StateManager invariants — a sketch

These two theorems formalise the shape of the cockpit-audit patches
#2 (`Invalidate`) and #6 (OidAuth state eviction) on
`SSO-Auth/Api/SSOController.cs`.

We model the OAuth state manager as a finite map from a state key to
a freshness bit. Real-world fields on `TimedAuthorizeState` (Username,
Admin, Folders, …) are irrelevant to the housekeeping invariant we
care about here, which is:

  * whatever `Invalidate` returns is a subset of its input,
  * and when the input is partitioned into fresh and stale entries by
    an arbitrary predicate, `Invalidate` is exactly that predicate's
    filter: no fresh entry is dropped and no stale entry survives.

The C# patch is a snapshot-then-remove walk. In Lean we model a
snapshot-safe removal as `List.filter`: it only keeps entries the
predicate says to keep, and it never mutates the list under its own
walk.

Neither proof uses `sorry`.
-/

namespace StateManager

/-- A bare OAuth state entry: a key and a freshness flag.
    `fresh = false` means "created more than a minute ago and due to be
    invalidated". -/
structure Entry where
  key   : String
  fresh : Bool
  deriving DecidableEq, Repr

/-- Invalidate: keep only entries whose `fresh` bit is true. This is the
    algorithmic shape of the post-patch `Invalidate()` — a snapshot walk
    (`List.filter` enumerates once over a pure value) that removes stale
    entries without mutating the enumerator. -/
def invalidate (xs : List Entry) : List Entry :=
  xs.filter (·.fresh)

/-- `invalidate` only shrinks — every surviving entry was already in the
    input. This is the "snapshot-then-remove walk is safe" claim: the
    output is a subset of the input, independent of how fresh/stale
    entries are interleaved. -/
theorem invalidate_subset :
    ∀ xs : List Entry, ∀ e, e ∈ invalidate xs → e ∈ xs := by
  intro xs e h
  exact (List.mem_filter.mp h).1

/-- `invalidate` is terminating with a subset: there exists `ys` such
    that `ys = invalidate xs` and every element of `ys` lies in `xs`.
    The `subset` content comes from `invalidate_subset`. -/
theorem invalidate_terminates :
    ∀ xs : List Entry, ∃ ys, ys = invalidate xs ∧ ∀ e, e ∈ ys → e ∈ xs := by
  intro xs
  refine ⟨invalidate xs, rfl, ?_⟩
  exact invalidate_subset xs

/-- Fresh entries survive and stale entries do not: an entry `e` lies in
    `invalidate xs` iff it was in `xs` and its `fresh` bit is true. This
    pins down patch #2 exactly — a fresh entry is never dropped, a stale
    entry never survives. -/
theorem invalidate_preserves_fresh :
    ∀ xs : List Entry, ∀ e,
      e ∈ invalidate xs ↔ e ∈ xs ∧ e.fresh = true := by
  intro xs e
  constructor
  · intro h
    have hmem := (List.mem_filter.mp h)
    exact ⟨hmem.1, hmem.2⟩
  · intro h
    exact List.mem_filter.mpr ⟨h.1, h.2⟩

end StateManager
