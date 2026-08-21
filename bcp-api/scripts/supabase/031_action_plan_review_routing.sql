-- A review on an action can now be addressed to a department or an individual,
-- rather than being a note nobody is asked to answer. The sender keeps their own
-- copy through reviewer_id, so a routed review shows up for both sides.

alter table public.analysis_action_plan_reviews
  add column if not exists assignee_type text,
  add column if not exists assignee_department_id uuid references public.departments(id) on delete set null,
  add column if not exists assignee_user_id uuid,
  add column if not exists assignee_label text;

do $$
begin
  if not exists (
    select 1 from pg_constraint where conname = 'analysis_action_plan_reviews_assignee_type_check'
  ) then
    alter table public.analysis_action_plan_reviews
      add constraint analysis_action_plan_reviews_assignee_type_check
      check (assignee_type is null or assignee_type in ('department', 'user'));
  end if;
end $$;

create index if not exists idx_action_plan_reviews_assignee_user
  on public.analysis_action_plan_reviews (assignee_user_id)
  where assignee_user_id is not null;

create index if not exists idx_action_plan_reviews_assignee_department
  on public.analysis_action_plan_reviews (assignee_department_id)
  where assignee_department_id is not null;

comment on column public.analysis_action_plan_reviews.assignee_type is
  'department | user — who the review was routed to; null means it is a plain note';
