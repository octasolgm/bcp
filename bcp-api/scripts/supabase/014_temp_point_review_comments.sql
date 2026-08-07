-- Temporary manual review notes per analysis point (remove table when workflow is finalized).

CREATE TABLE IF NOT EXISTS temp_point_review_comments (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  analysis_point_id UUID NOT NULL REFERENCES analysis_points(id) ON DELETE CASCADE,
  comment TEXT NOT NULL,
  commented_by UUID,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_temp_point_review_comments_point
  ON temp_point_review_comments (analysis_point_id);

ALTER TABLE temp_point_review_comments ENABLE ROW LEVEL SECURITY;

CREATE POLICY temp_point_review_comments_super_admin_all ON temp_point_review_comments
  FOR ALL USING (public.is_nd_super_admin());

CREATE POLICY temp_point_review_comments_read ON temp_point_review_comments
  FOR SELECT USING (public.is_nd_authenticated());

CREATE POLICY temp_point_review_comments_insert ON temp_point_review_comments
  FOR INSERT WITH CHECK (public.is_nd_authenticated());

CREATE POLICY temp_point_review_comments_delete_own ON temp_point_review_comments
  FOR DELETE USING (
    public.is_nd_super_admin()
    OR commented_by = auth.uid()
  );
