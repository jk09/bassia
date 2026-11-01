/-!
# A model of a Bassia component store and a working copy

The model follows the storage contract in `Bassia/Storage` (see `MemoryComponentStore` and `MemoryWorkspace`, the
reference implementation):

* a component store holds commits by id (`Store.objects`) and branches naming commits (`Store.branches`);
* a commit records its parents and a snapshot of the working files, its *tree* (`Commit.tree`; the type of trees `T`
  is left abstract - a tree is whatever a snapshot of the files is);
* a working copy (`Workspace`) is checked out at a base commit and makes commits on top of it, newest first
  (`Workspace.made`); no branch of the store names them until the working copy is published.

Objects are never rewritten: adding commits to a store only fills ids it does not hold yet (`overlay`), as writing an
object whose id a git object store already holds leaves it as it was.
-/

namespace Bassia

abbrev CommitId := Nat
abbrev BranchName := String

/-- A commit: its parents (the first one is the commit it was made on) and its tree. -/
structure Commit (T : Type) where
  parents : List CommitId
  tree : T

/-- A component store: commits by id and branches by name. -/
structure Store (T : Type) where
  objects : CommitId → Option (Commit T)
  branches : BranchName → Option CommitId

variable {T : Type}

/-- The object map holds commit `i`. -/
abbrev Present (o : CommitId → Option (Commit T)) (i : CommitId) : Prop := ∃ c, o i = some c

/-- Every parent of a commit the object map holds is held too (a store never holds a dangling parent). -/
abbrev Closed (o : CommitId → Option (Commit T)) : Prop :=
  ∀ (i : CommitId) (c : Commit T) (p : CommitId), o i = some c → p ∈ c.parents → Present o p

/-- `Reaches o a b`: following parents from `a` arrives at `b` (`b` is `a` or one of its ancestors). -/
inductive Reaches (o : CommitId → Option (Commit T)) : CommitId → CommitId → Prop
  | refl (a : CommitId) : Reaches o a a
  | parent {a p b : CommitId} (c : Commit T) : o a = some c → p ∈ c.parents → Reaches o p b → Reaches o a b

theorem Reaches.mono {o o' : CommitId → Option (Commit T)}
    (hsub : ∀ (i : CommitId) (c : Commit T), o i = some c → o' i = some c) {a b : CommitId}
    (h : Reaches o a b) : Reaches o' a b := by
  induction h with
  | refl a => exact Reaches.refl a
  | @parent a p b c hc hp _ ih => exact Reaches.parent c (hsub a c hc) hp ih

theorem Reaches.trans {o : CommitId → Option (Commit T)} {a b d : CommitId}
    (h₁ : Reaches o a b) (h₂ : Reaches o b d) : Reaches o a d := by
  revert h₂
  induction h₁ with
  | refl _ => exact fun h => h
  | @parent x p y c hc hp _ ih => exact fun h => Reaches.parent c hc hp (ih h)

/--
The first-parent history from commit `i`, newest first, as the trees of its commits: at most `n` of them, and it stops
at a commit the object map does not hold.
-/
def history (o : CommitId → Option (Commit T)) : Nat → CommitId → List T
  | 0, _ => []
  | n + 1, i =>
    match o i with
    | none => []
    | some c =>
      match c.parents with
      | [] => [c.tree]
      | p :: _ => c.tree :: history o n p

theorem history_step {o : CommitId → Option (Commit T)} {i : CommitId} {c : Commit T} {p : CommitId}
    {ps : List CommitId} {n : Nat} (h : o i = some c) (hp : c.parents = p :: ps) :
    history o (n + 1) i = c.tree :: history o n p := by
  simp [history, h, hp]

/-- A larger object map that agrees with a closed one has the same history from every commit the closed one holds. -/
theorem history_ext {o o' : CommitId → Option (Commit T)}
    (hsub : ∀ (i : CommitId) (c : Commit T), o i = some c → o' i = some c) (hclosed : Closed o) :
    ∀ (n : Nat) (i : CommitId), Present o i → history o' n i = history o n i := by
  intro n
  induction n with
  | zero => intro i _; simp [history]
  | succ n ih =>
    intro i hi
    obtain ⟨c, hc⟩ := hi
    have hc' := hsub i c hc
    cases hp : c.parents with
    | nil => simp [history, hc, hc', hp]
    | cons p ps =>
      have hpres : Present o p := hclosed i c p hc (by rw [hp]; simp)
      simp [history, hc, hc', hp, ih p hpres]

/-! ## Commits a working copy made, and adding them to a store -/

/-- The commit with id `i` among `made`, the first one listed. -/
def lookup : List (CommitId × Commit T) → CommitId → Option (Commit T)
  | [], _ => none
  | (j, c) :: rest, i => if i = j then some c else lookup rest i

theorem lookup_cons_self (j : CommitId) (c : Commit T) (rest : List (CommitId × Commit T)) :
    lookup ((j, c) :: rest) j = some c := by
  simp [lookup]

/--
The object map after adding the commits `made`: a commit the store already holds stays as it is; any other id is
looked up among `made`.
-/
def overlay (objs : CommitId → Option (Commit T)) (made : List (CommitId × Commit T)) (i : CommitId) :
    Option (Commit T) :=
  match objs i with
  | some c => some c
  | none => lookup made i

theorem overlay_sup (objs : CommitId → Option (Commit T)) (made : List (CommitId × Commit T)) :
    ∀ (i : CommitId) (c : Commit T), objs i = some c → overlay objs made i = some c := by
  intro i c h
  simp [overlay, h]

theorem overlay_nil (objs : CommitId → Option (Commit T)) (i : CommitId) : overlay objs [] i = objs i := by
  cases h : objs i <;> simp [overlay, h, lookup]

theorem overlay_new {objs : CommitId → Option (Commit T)} {j : CommitId} {c : Commit T}
    {rest : List (CommitId × Commit T)} (h : objs j = none) : overlay objs ((j, c) :: rest) j = some c := by
  simp [overlay, h, lookup]

theorem overlay_none_left {objs : CommitId → Option (Commit T)} {made : List (CommitId × Commit T)}
    {i : CommitId} (h : overlay objs made i = none) : objs i = none := by
  cases ho : objs i with
  | none => rfl
  | some c => simp [overlay, ho] at h

theorem overlay_none_right {objs : CommitId → Option (Commit T)} {made : List (CommitId × Commit T)}
    {i : CommitId} (h : overlay objs made i = none) : lookup made i = none := by
  have h1 := overlay_none_left h
  simpa [overlay, h1] using h

theorem overlay_cons_ne {objs : CommitId → Option (Commit T)} {made : List (CommitId × Commit T)}
    {i j : CommitId} {c : Commit T} (h1 : objs i = none) (h2 : ¬ i = j) (h3 : lookup made i = none) :
    overlay objs ((j, c) :: made) i = none := by
  simp [overlay, h1, lookup, h2, h3]

/-- Adding one more commit, with an id not among `made`, keeps every commit the object map held. -/
theorem overlay_cons_sub {objs : CommitId → Option (Commit T)} {made : List (CommitId × Commit T)}
    {j : CommitId} {c' : Commit T} (hj : lookup made j = none) :
    ∀ (i : CommitId) (c : Commit T), overlay objs made i = some c → overlay objs ((j, c') :: made) i = some c := by
  intro i c h
  cases ho : objs i with
  | some d => simpa [overlay, ho] using h
  | none =>
    simp [overlay, ho] at h ⊢
    have hij : ¬ i = j := by
      intro e
      rw [e, hj] at h
      cases h
    simpa [lookup, hij] using h

/-- A working copy: the commit it was checked out at and the commits it made since, newest first. -/
structure Workspace (T : Type) where
  base : CommitId
  made : List (CommitId × Commit T)

/-- The commit a working copy is at: its newest commit, or its base before it made any. -/
def Workspace.head (ws : Workspace T) : CommitId :=
  match ws.made with
  | [] => ws.base
  | (j, _) :: _ => j

/-- Committing the working tree `t` as commit `j`: one parent, the head (`IComponentWorkspace.CommitAsync`). -/
def Workspace.commit (ws : Workspace T) (j : CommitId) (t : T) : Workspace T :=
  { ws with made := (j, { parents := [ws.head], tree := t }) :: ws.made }

/-- Concluding a merge of commit `other` with the tree `t` as commit `j`: a merge commit, parents head and `other`. -/
def Workspace.merge (ws : Workspace T) (j other : CommitId) (t : T) : Workspace T :=
  { ws with made := (j, { parents := [ws.head, other], tree := t }) :: ws.made }

theorem Workspace.head_commit (ws : Workspace T) (j : CommitId) (t : T) : (ws.commit j t).head = j := rfl

theorem Workspace.head_merge (ws : Workspace T) (j other : CommitId) (t : T) : (ws.merge j other t).head = j := rfl

/-- A working copy that made commits and merges, each under an id it had not used yet. -/
inductive Made : Workspace T → Prop
  | start (base : CommitId) : Made { base := base, made := [] }
  | commit {ws : Workspace T} (j : CommitId) (t : T) :
      Made ws → lookup ws.made j = none → Made (ws.commit j t)
  | merge {ws : Workspace T} (j other : CommitId) (t : T) :
      Made ws → lookup ws.made j = none → Made (ws.merge j other t)

/-- A working copy that made commits only - no merge commits - each under an id it had not used yet. -/
inductive Built : Workspace T → Prop
  | start (base : CommitId) : Built { base := base, made := [] }
  | commit {ws : Workspace T} (j : CommitId) (t : T) :
      Built ws → lookup ws.made j = none → Built (ws.commit j t)

theorem Built.toMade : ∀ {ws : Workspace T}, Built ws → Made ws := by
  intro ws hb
  induction hb with
  | start base => exact Made.start base
  | @commit ws j t _ hj ih => exact Made.commit j t ih hj

/--
The ids of the commits a working copy made are new to the store: commit ids are content hashes, so a commit the store
already held would be the same commit.
-/
abbrev Fresh (objs : CommitId → Option (Commit T)) (ws : Workspace T) : Prop :=
  ∀ (i : CommitId) (c : Commit T), lookup ws.made i = some c → objs i = none

theorem Fresh.of_cons {objs : CommitId → Option (Commit T)} {ws : Workspace T} {j : CommitId} {c : Commit T}
    (h : ∀ (i : CommitId) (d : Commit T), lookup ((j, c) :: ws.made) i = some d → objs i = none) :
    Fresh objs ws := by
  intro i d hi
  by_cases hij : i = j
  · rw [hij]
    exact h j c (lookup_cons_self j c ws.made)
  · exact h i d (by simp [lookup, hij, hi])

/-- The trees of the commits a working copy made, newest first. -/
def treesOf : List (CommitId × Commit T) → List T
  | [] => []
  | (_, c) :: rest => c.tree :: treesOf rest

/-- The head of a working copy, with its fresh commits added to the store, reaches the base it was checked out at. -/
theorem Made.reaches_base {objs : CommitId → Option (Commit T)} :
    ∀ {ws : Workspace T}, Made ws → Fresh objs ws → Reaches (overlay objs ws.made) ws.head ws.base := by
  intro ws hm
  induction hm with
  | start base => intro _; exact Reaches.refl _
  | @commit ws j t _ hj ih =>
    intro hfresh
    have hjnone : objs j = none :=
      hfresh j { parents := [ws.head], tree := t } (lookup_cons_self j _ ws.made)
    have hlift := Reaches.mono (overlay_cons_sub (objs := objs) (c' := { parents := [ws.head], tree := t }) hj)
      (ih (Fresh.of_cons hfresh))
    show Reaches (overlay objs ((j, { parents := [ws.head], tree := t }) :: ws.made)) j ws.base
    exact Reaches.parent _ (overlay_new hjnone) (by simp) hlift
  | @merge ws j other t _ hj ih =>
    intro hfresh
    have hjnone : objs j = none :=
      hfresh j { parents := [ws.head, other], tree := t } (lookup_cons_self j _ ws.made)
    have hlift := Reaches.mono
      (overlay_cons_sub (objs := objs) (c' := { parents := [ws.head, other], tree := t }) hj)
      (ih (Fresh.of_cons hfresh))
    show Reaches (overlay objs ((j, { parents := [ws.head, other], tree := t }) :: ws.made)) j ws.base
    exact Reaches.parent _ (overlay_new hjnone) (by simp) hlift

/--
Adding the commits of a working copy without merges to a closed store keeps it closed, and holds the working copy's
head (given the store holds its base).
-/
theorem Built.closed {objs : CommitId → Option (Commit T)} (hclosed : Closed objs) :
    ∀ {ws : Workspace T}, Built ws → Present objs ws.base →
      Closed (overlay objs ws.made) ∧ Present (overlay objs ws.made) ws.head := by
  intro ws hb
  induction hb with
  | start base =>
    intro hbase
    show Closed (overlay objs []) ∧ Present (overlay objs []) base
    refine ⟨?_, ?_⟩
    · intro i c p hc hp
      rw [overlay_nil] at hc
      obtain ⟨d, hd⟩ := hclosed i c p hc hp
      exact ⟨d, overlay_sup objs [] p d hd⟩
    · obtain ⟨d, hd⟩ := hbase
      exact ⟨d, overlay_sup objs [] base d hd⟩
  | @commit ws j t _ hj ih =>
    intro hbase
    obtain ⟨hcl, hpr⟩ := ih hbase
    have hsub := overlay_cons_sub (objs := objs) (c' := { parents := [ws.head], tree := t }) hj
    show Closed (overlay objs ((j, { parents := [ws.head], tree := t }) :: ws.made)) ∧
      Present (overlay objs ((j, { parents := [ws.head], tree := t }) :: ws.made)) j
    refine ⟨?_, ?_⟩
    · intro i c p hc hp
      cases ho : overlay objs ws.made i with
      | some d =>
        rw [hsub i d ho] at hc
        have hdc : d = c := Option.some.inj hc
        rw [← hdc] at hp
        obtain ⟨e, he⟩ := hcl i d p ho hp
        exact ⟨e, hsub p e he⟩
      | none =>
        have hoi : objs i = none := overlay_none_left ho
        have hli : lookup ws.made i = none := overlay_none_right ho
        by_cases hij : i = j
        · rw [hij] at hc hoi
          rw [overlay_new hoi] at hc
          have hcc := Option.some.inj hc
          rw [← hcc] at hp
          have hpe : p = ws.head := by simpa using hp
          rw [hpe]
          obtain ⟨e, he⟩ := hpr
          exact ⟨e, hsub ws.head e he⟩
        · rw [overlay_cons_ne hoi hij hli] at hc
          cases hc
    · cases h' : objs j with
      | some e => exact ⟨e, overlay_sup objs _ j e h'⟩
      | none => exact ⟨{ parents := [ws.head], tree := t }, overlay_new h'⟩

/--
**The history of a working copy without merge commits.** With its commits added to a closed store holding its base,
the first-parent history from its head is the trees it committed, newest first, followed by the history of the base -
for any length of the latter.
-/
theorem Built.history_eq {objs : CommitId → Option (Commit T)} (hclosed : Closed objs) :
    ∀ {ws : Workspace T}, Built ws → Present objs ws.base → Fresh objs ws →
      ∀ n : Nat, history (overlay objs ws.made) (ws.made.length + n) ws.head =
        treesOf ws.made ++ history objs n ws.base := by
  intro ws hb
  induction hb with
  | start base =>
    intro hbase _ n
    show history (overlay objs []) (0 + n) base = [] ++ history objs n base
    rw [Nat.zero_add, List.nil_append]
    exact history_ext (overlay_sup objs []) hclosed n base hbase
  | @commit ws j t hws hj ih =>
    intro hbase hfresh n
    have hjnone : objs j = none :=
      hfresh j { parents := [ws.head], tree := t } (lookup_cons_self j _ ws.made)
    have hsub := overlay_cons_sub (objs := objs) (c' := { parents := [ws.head], tree := t }) hj
    obtain ⟨hcl, hpr⟩ := Built.closed hclosed hws hbase
    show history (overlay objs ((j, { parents := [ws.head], tree := t }) :: ws.made)) (ws.made.length + 1 + n) j =
      treesOf ((j, { parents := [ws.head], tree := t }) :: ws.made) ++ history objs n ws.base
    have e : ws.made.length + 1 + n = ws.made.length + n + 1 := by omega
    rw [e, history_step (c := { parents := [ws.head], tree := t }) (overlay_new (rest := ws.made) hjnone) rfl,
      history_ext hsub hcl (ws.made.length + n) ws.head hpr, ih hbase (Fresh.of_cons hfresh) n]
    all_goals rfl

end Bassia
