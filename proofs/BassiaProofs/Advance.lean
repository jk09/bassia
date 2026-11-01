import BassiaProofs.Model

/-!
# Landing an integration: advancing base branches

`IntegrationRunner.AdvanceAsync` lands an integration by `IComponentStorage.MoveBranchesAsync`: each integrated
component's base branch moves to the integration's result commit (already published to the component's store), but
only while it still points at the commit the integration was built on - a compare-and-swap - and all of them or none
(`BranchMoves.InSequenceAsync` checks every expectation before it moves anything and puts moved branches back if a
compare-and-swap fails in between).

The model is the contract: `moveBranches` either applies every move or refuses (`none`), leaving the monorepo as it was.
-/

namespace Bassia

variable {T : Type}

abbrev ComponentName := String

/-- The component stores of a monorepo by name. -/
abbrev Monorepo (T : Type) := ComponentName → Store T

/-- A branch to move (`BranchMove`): component, branch, the commit it is to point at and the one it must point at now. -/
structure Move where
  component : ComponentName
  branch : BranchName
  commit : CommitId
  expected : CommitId

/-- The first move of `moves` for branch `br` of component `comp`. -/
def findMove (comp : ComponentName) (br : BranchName) : List Move → Option Move
  | [] => none
  | m :: ms => if m.component = comp ∧ m.branch = br then some m else findMove comp br ms

/-- At most one move per branch of a component (an integration advances one base branch per component). -/
abbrev OnePerBranch (moves : List Move) : Prop :=
  ∀ m₁ ∈ moves, ∀ m₂ ∈ moves, m₁.component = m₂.component → m₁.branch = m₂.branch → m₁ = m₂

theorem findMove_of_mem {m : Move} : ∀ {moves : List Move}, m ∈ moves →
    (∀ m' ∈ moves, m'.component = m.component → m'.branch = m.branch → m' = m) →
    findMove m.component m.branch moves = some m := by
  intro moves
  induction moves with
  | nil => intro hm; simp at hm
  | cons x xs ih =>
    intro hm hu
    by_cases hx : x.component = m.component ∧ x.branch = m.branch
    · have hxm : x = m := hu x (List.mem_cons.mpr (Or.inl rfl)) hx.1 hx.2
      simp only [findMove]
      rw [if_pos hx, hxm]
    · have hmxs : m ∈ xs := by
        rcases List.mem_cons.mp hm with h | h
        · subst h
          exact (hx ⟨rfl, rfl⟩).elim
        · exact h
      simp only [findMove]
      rw [if_neg hx]
      exact ih hmxs (fun m' hm' => hu m' (List.mem_cons.mpr (Or.inr hm')))

/-- Every branch named by a move points at the move's commit; every other branch and every object stays. -/
def applyMoves (moves : List Move) (r : Monorepo T) : Monorepo T :=
  fun comp =>
    { objects := (r comp).objects
      branches := fun br =>
        match findMove comp br moves with
        | some m => some m.commit
        | none => (r comp).branches br }

/-- Every branch to move still points at the commit expected. -/
def Expected (moves : List Move) (r : Monorepo T) : Prop :=
  ∀ m ∈ moves, (r m.component).branches m.branch = some m.expected

open Classical in
/-- `MoveBranchesAsync`: every move, or - when a branch has moved meanwhile - none. -/
noncomputable def moveBranches (moves : List Move) (r : Monorepo T) : Option (Monorepo T) :=
  if Expected moves r then some (applyMoves moves r) else none

open Classical in
theorem moveBranches_some {moves : List Move} {r r' : Monorepo T}
    (h : moveBranches moves r = some r') : Expected moves r ∧ r' = applyMoves moves r := by
  unfold moveBranches at h
  by_cases he : Expected moves r
  · rw [if_pos he] at h
    exact ⟨he, (Option.some.inj h).symm⟩
  · rw [if_neg he] at h
    cases h

open Classical in
/-- One stale expectation refuses the whole move: no branch of any component moves. -/
theorem moveBranches_refused {moves : List Move} {r : Monorepo T} {m : Move}
    (hm : m ∈ moves) (hstale : ¬ (r m.component).branches m.branch = some m.expected) :
    moveBranches moves r = none := by
  have he : ¬ Expected moves r := fun he => hstale (he m hm)
  unfold moveBranches
  rw [if_neg he]

/-- After the move, every branch named by a move points at its commit: the integration landed as a whole. -/
theorem moveBranches_tip {moves : List Move} {r r' : Monorepo T} {m : Move}
    (h : moveBranches moves r = some r') (hu : OnePerBranch moves) (hm : m ∈ moves) :
    (r' m.component).branches m.branch = some m.commit := by
  obtain ⟨_, rfl⟩ := moveBranches_some h
  have hf := findMove_of_mem hm (fun m' hm' hc hb => hu m' hm' m hm hc hb)
  simp [applyMoves, hf]

/-- Branches no move names stay where they were. -/
theorem moveBranches_other {moves : List Move} {r r' : Monorepo T} {comp : ComponentName} {br : BranchName}
    (h : moveBranches moves r = some r') (hn : findMove comp br moves = none) :
    (r' comp).branches br = (r comp).branches br := by
  obtain ⟨_, rfl⟩ := moveBranches_some h
  simp [applyMoves, hn]

/-- Moving branches writes no object: every commit, with its parents and its tree, stays as it was. -/
theorem moveBranches_objects {moves : List Move} {r r' : Monorepo T}
    (h : moveBranches moves r = some r') (comp : ComponentName) : (r' comp).objects = (r comp).objects := by
  obtain ⟨_, rfl⟩ := moveBranches_some h
  rfl

/--
**Advancing an integration.** When the move of every integrated component's base branch succeeds, each base branch
points at its integration result - whose commit, and so whose tree, is exactly the one the integration published -
and, the result being built on the base the branch had (`Reaches result expected`, as an integration starts its
checkout at the base), the new tip reaches every commit the branch reached before. This holds with merge commits:
an integration merges each run's result with a merge commit whose first parent is the base side.
-/
theorem advance_correct {moves : List Move} {r r' : Monorepo T} {m : Move}
    (h : moveBranches moves r = some r') (hu : OnePerBranch moves) (hm : m ∈ moves)
    (hbuilt : Reaches (r m.component).objects m.commit m.expected) :
    (r m.component).branches m.branch = some m.expected ∧
      (r' m.component).branches m.branch = some m.commit ∧
      (r' m.component).objects m.commit = (r m.component).objects m.commit ∧
      (∀ x : CommitId, Reaches (r m.component).objects m.expected x →
        Reaches (r' m.component).objects m.commit x) := by
  have htip := moveBranches_tip h hu hm
  have hobj := moveBranches_objects h m.component
  obtain ⟨he, _⟩ := moveBranches_some h
  refine ⟨he m hm, htip, by rw [hobj], ?_⟩
  intro x hx
  rw [hobj]
  exact Reaches.trans hbuilt hx

/--
The result of an integration built in a working copy checked out at the base (`Made`: commits and merges) reaches
that base once its commits are in the store - the premise `advance_correct` needs.
-/
theorem integration_reaches_base {objs : CommitId → Option (Commit T)} {ws : Workspace T}
    (hm : Made ws) (hfresh : Fresh objs ws) : Reaches (overlay objs ws.made) ws.head ws.base :=
  Made.reaches_base hm hfresh

end Bassia
