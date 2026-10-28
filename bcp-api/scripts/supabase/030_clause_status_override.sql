-- Clause compliance status can now diverge from what the AI pipeline decided:
--   * a reviewer may override it by hand
--   * it flips to compliant automatically once every action on the clause is resolved
--
-- final_status_source records which of those happened, so the auto rule never
-- overwrites a human decision, and ai_final_status keeps the pipeline's own verdict
-- so an auto flip can be undone when an action is reopened.

alter table public.analysis_points
  add column if not exists final_status_source text,
  add column if not exists ai_final_status text;

do $$
begin
  if not exists (
    select 1 from pg_constraint where conname = 'analysis_points_final_status_source_check'
  ) then
    alter table public.analysis_points
      add constraint analysis_points_final_status_source_check
      check (final_status_source is null or final_status_source in ('manual', 'auto'));
  end if;
end $$;

comment on column public.analysis_points.final_status_source is
  'null = AI pipeline, manual = set by a user, auto = flipped because all actions resolved';
comment on column public.analysis_points.ai_final_status is
  'final_status as the AI pipeline last set it, kept so an auto flip can be reverted';
