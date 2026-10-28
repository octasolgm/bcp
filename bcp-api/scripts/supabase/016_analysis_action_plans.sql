-- 016: Corrective action plans per gap, their review comments, and target-date audit trail.
-- Replaces the planning half of action_plan_item_reviews (kept for backward compatibility).

CREATE TABLE IF NOT EXISTS analysis_action_plans (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  analysis_run_id UUID NOT NULL REFERENCES analysis_runs(id) ON DELETE CASCADE,
  analysis_point_id UUID NOT NULL REFERENCES analysis_points(id) ON DELETE CASCADE,
  gap_index INT NOT NULL DEFAULT 0,
  action_plan TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL DEFAULT 'pending',
  priority TEXT NOT NULL DEFAULT 'medium',
  priority_score INT NOT NULL DEFAULT 50,
  target_date TIMESTAMPTZ NULL,
  responsibility_type TEXT NOT NULL DEFAULT 'department',
  responsibility_department_id UUID NULL,
  responsibility_user_id UUID NULL,
  responsibility_label TEXT NULL,
  comment TEXT NULL,
  sort_order INT NOT NULL DEFAULT 0,
  resolved_at TIMESTAMPTZ NULL,
  resolved_by UUID NULL,
  created_by UUID NULL,
  updated_by UUID NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Additive columns for pre-existing installs.
ALTER TABLE analysis_action_plans ADD COLUMN IF NOT EXISTS gap_index INT NOT NULL DEFAULT 0;
ALTER TABLE analysis_action_plans ADD COLUMN IF NOT EXISTS priority_score INT NOT NULL DEFAULT 50;

CREATE INDEX IF NOT EXISTS idx_analysis_action_plans_point
  ON analysis_action_plans (analysis_point_id, gap_index, sort_order);
CREATE INDEX IF NOT EXISTS idx_analysis_action_plans_run
  ON analysis_action_plans (analysis_run_id, priority, status);

-- Owners of an action. Several departments and/or people can share one action; each
-- row here feeds the assignee's inbox.
CREATE TABLE IF NOT EXISTS analysis_action_plan_assignees (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  action_plan_id UUID NOT NULL REFERENCES analysis_action_plans(id) ON DELETE CASCADE,
  assignee_type TEXT NOT NULL DEFAULT 'department',
  department_id UUID NULL,
  user_id UUID NULL,
  label TEXT NOT NULL DEFAULT '',
  sort_order INT NOT NULL DEFAULT 0,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_analysis_action_plan_assignees_plan
  ON analysis_action_plan_assignees (action_plan_id, sort_order);
CREATE INDEX IF NOT EXISTS idx_analysis_action_plan_assignees_user
  ON analysis_action_plan_assignees (user_id);
CREATE INDEX IF NOT EXISTS idx_analysis_action_plan_assignees_department
  ON analysis_action_plan_assignees (department_id);

CREATE TABLE IF NOT EXISTS analysis_action_plan_reviews (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  action_plan_id UUID NOT NULL REFERENCES analysis_action_plans(id) ON DELETE CASCADE,
  analysis_point_id UUID NOT NULL,
  analysis_run_id UUID NOT NULL,
  comment TEXT NOT NULL DEFAULT '',
  reviewer_id UUID NULL,
  reviewer_role TEXT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_analysis_action_plan_reviews_plan
  ON analysis_action_plan_reviews (action_plan_id, created_at);

CREATE TABLE IF NOT EXISTS analysis_action_plan_date_history (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  action_plan_id UUID NOT NULL REFERENCES analysis_action_plans(id) ON DELETE CASCADE,
  previous_target_date TIMESTAMPTZ NULL,
  new_target_date TIMESTAMPTZ NULL,
  reason TEXT NULL,
  changed_by UUID NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_analysis_action_plan_date_history_plan
  ON analysis_action_plan_date_history (action_plan_id, created_at DESC);
