import BassiaProofs.Model

/-!
# Landing a working copy: publishing it to a branch

`IComponentWorkspace.PublishAsync(branch, tags)` (see `MemoryWorkspace.PublishAsync`) adds the commits the working
copy made to the store and moves the branch to the working copy's head - only as a fast-forward: when the branch
exists, its tip must be an ancestor of the head, or nothing is published.

`publish` returns the store after landing, or `none` when the land is refused (and the store stays as it was).
-/

namespace Bassia

variable {T : Type}

/-- The branches with `b` moved to `tip`. -/
def setBranch (branches : BranchName → Option CommitId) (b : BranchName) (tip : CommitId) (x : BranchName) :
    Option CommitId :=
  if x = b then some tip else branches x

/-- The store once `ws` has landed on `b`: its commits added, `b` at its head. -/
def landed (s : Store T) (b : BranchName) (ws : Workspace T) : Store T :=
  { objects := overlay s.objects ws.made
    branches := setBranch s.branches b ws.head }

/-- The fast-forward check: the branch does not exist yet, or the head reaches its tip. -/
def FastForward (s : Store T) (b : BranchName) (ws : Workspace T) : Prop :=
  ∀ tip : CommitId, s.branches b = some tip → Reaches (overlay s.objects ws.made) ws.head tip

open Classical in
/-- Publishing `ws` to the branch `b` of `s`: `none` when it is not a fast-forward. -/
noncomputable def publish (s : Store T) (b : BranchName) (ws : Workspace T) : Option (Store T) :=
  if FastForward s b ws then some (landed s b ws) else none

open Classical in
theorem publish_some {s s' : Store T} {b : BranchName} {ws : Workspace T}
    (h : publish s b ws = some s') : FastForward s b ws ∧ s' = landed s b ws := by
  unfold publish at h
  by_cases hff : FastForward s b ws
  · rw [if_pos hff] at h
    exact ⟨hff, (Option.some.inj h).symm⟩
  · rw [if_neg hff] at h
    cases h

open Classical in
/-- A land that is not a fast-forward is refused; the store is left as it was. -/
theorem publish_refused {s : Store T} {b : BranchName} {ws : Workspace T}
    (h : ¬ FastForward s b ws) : publish s b ws = none := by
  unfold publish
  rw [if_neg h]

open Classical in
/-- A working copy checked out at the branch's tip always lands (its commits being new to the store). -/
theorem publish_succeeds {s : Store T} {b : BranchName} {ws : Workspace T}
    (hm : Made ws) (hfresh : Fresh s.objects ws) (htip : s.branches b = some ws.base) :
    ∃ s', publish s b ws = some s' := by
  have hff : FastForward s b ws := by
    unfold FastForward
    intro tip h
    rw [htip] at h
    have e : ws.base = tip := Option.some.inj h
    rw [← e]
    exact Made.reaches_base hm hfresh
  refine ⟨landed s b ws, ?_⟩
  unfold publish
  rw [if_pos hff]

/-- After landing, the branch points at the working copy's head. -/
theorem publish_tip {s s' : Store T} {b : BranchName} {ws : Workspace T}
    (h : publish s b ws = some s') : s'.branches b = some ws.head := by
  obtain ⟨_, rfl⟩ := publish_some h
  simp [landed, setBranch]

/-- Landing on `b` moves no other branch. -/
theorem publish_other_branches {s s' : Store T} {b x : BranchName} {ws : Workspace T}
    (h : publish s b ws = some s') (hx : ¬ x = b) : s'.branches x = s.branches x := by
  obtain ⟨_, rfl⟩ := publish_some h
  simp [landed, setBranch, hx]

/-- Landing changes no commit the store held: same id, same parents, same tree. -/
theorem publish_keeps_objects {s s' : Store T} {b : BranchName} {ws : Workspace T}
    (h : publish s b ws = some s') :
    ∀ (i : CommitId) (c : Commit T), s.objects i = some c → s'.objects i = some c := by
  obtain ⟨_, rfl⟩ := publish_some h
  exact overlay_sup s.objects ws.made

/--
After landing, the new tip is the commit the working copy made last, exactly as it made it - so the branch carries
the very tree the working copy committed. This holds with merge commits too.
-/
theorem publish_tip_commit {s s' : Store T} {b : BranchName} {ws : Workspace T} {j : CommitId} {c : Commit T}
    {rest : List (CommitId × Commit T)} (hmade : ws.made = (j, c) :: rest) (hfresh : Fresh s.objects ws)
    (h : publish s b ws = some s') : s'.branches b = some j ∧ s'.objects j = some c := by
  have hhead : ws.head = j := by simp [Workspace.head, hmade]
  have hl : lookup ws.made j = some c := by rw [hmade]; exact lookup_cons_self j c rest
  have hj : s.objects j = none := hfresh j c hl
  refine ⟨by rw [← hhead]; exact publish_tip h, ?_⟩
  obtain ⟨_, rfl⟩ := publish_some h
  show overlay s.objects ws.made j = some c
  rw [hmade]
  exact overlay_new hj

/-- Committing the working tree `t` and landing it: the branch's tip has the tree `t`. -/
theorem publish_commit_tree {s s' : Store T} {b : BranchName} {ws : Workspace T} {j : CommitId} {t : T}
    (hfresh : Fresh s.objects (ws.commit j t)) (h : publish s b (ws.commit j t) = some s') :
    s'.branches b = some j ∧ ∃ c, s'.objects j = some c ∧ c.tree = t := by
  have r := publish_tip_commit (ws := ws.commit j t) (j := j) (c := { parents := [ws.head], tree := t })
    (rest := ws.made) rfl hfresh h
  exact ⟨r.1, _, r.2, rfl⟩

/--
Landing keeps every earlier commit reachable: whatever the branch reached before (every commit, and so every tree, of
its history) the new tip reaches, unchanged.
-/
theorem publish_keeps_reachable {s s' : Store T} {b : BranchName} {ws : Workspace T} {old x : CommitId}
    (h : publish s b ws = some s') (hold : s.branches b = some old) (hr : Reaches s.objects old x) :
    s'.branches b = some ws.head ∧ Reaches s'.objects ws.head x := by
  refine ⟨publish_tip h, ?_⟩
  obtain ⟨hff, rfl⟩ := publish_some h
  exact Reaches.trans (hff old hold) (Reaches.mono (overlay_sup s.objects ws.made) hr)

/--
Without merge commits, the first-parent history of the landed branch is the trees the working copy committed, newest
first, followed by the history of the commit it was checked out at.
-/
theorem publish_history {s s' : Store T} {b : BranchName} {ws : Workspace T}
    (hb : Built ws) (hclosed : Closed s.objects) (hbase : Present s.objects ws.base)
    (hfresh : Fresh s.objects ws) (h : publish s b ws = some s') (n : Nat) :
    history s'.objects (ws.made.length + n) ws.head = treesOf ws.made ++ history s.objects n ws.base := by
  obtain ⟨_, rfl⟩ := publish_some h
  exact Built.history_eq hclosed hb hbase hfresh n

/--
**Landing a working copy without merge commits.** A working copy checked out at the tip of branch `b`, which made
commits only - the last one committing the working tree `t` as commit `j` - lands on `b`, and then

1. `b` points at `j`, whose tree is `t`: the branch carries exactly the tree the working copy produced;
2. every commit the store held is still there, with the same parents and the same tree;
3. every commit the old tip reached, the new tip reaches;
4. the first-parent history of `b` is the trees the working copy committed, newest first, followed by every tree of
   the branch's earlier history, in order.
-/
theorem landing_correct {s : Store T} {b : BranchName} {ws : Workspace T} {j : CommitId} {t : T}
    (hclosed : Closed s.objects)
    (hbuilt : Built (ws.commit j t))
    (htip : s.branches b = some ws.base)
    (hbase : Present s.objects ws.base)
    (hfresh : Fresh s.objects (ws.commit j t)) :
    ∃ s', publish s b (ws.commit j t) = some s' ∧
      s'.branches b = some j ∧
      (∃ c, s'.objects j = some c ∧ c.tree = t) ∧
      (∀ (i : CommitId) (c : Commit T), s.objects i = some c → s'.objects i = some c) ∧
      (∀ x : CommitId, Reaches s.objects ws.base x → Reaches s'.objects j x) ∧
      (∀ n : Nat, history s'.objects ((ws.commit j t).made.length + n) j =
        treesOf (ws.commit j t).made ++ history s.objects n ws.base) := by
  obtain ⟨s', hs'⟩ := publish_succeeds (Built.toMade hbuilt) hfresh htip
  refine ⟨s', hs', (publish_commit_tree hfresh hs').1, (publish_commit_tree hfresh hs').2,
    publish_keeps_objects hs', ?_, ?_⟩
  · intro x hx
    exact (publish_keeps_reachable hs' htip hx).2
  · intro n
    exact publish_history hbuilt hclosed hbase hfresh hs' n

end Bassia
