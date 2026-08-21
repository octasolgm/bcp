import type { CapGap } from '../ai-lab/parse-reference-response';
import { normalizeGapRisk, defaultTargetDateForGapRisk } from './doc-analysis-ready';
import type { ActionPlanPriority } from './action-plan';

/**
 * Demo feed for the first draft of an action plan. Every entry is editable in the UI
 * afterwards — this only decides what the maker sees before they touch anything.
 *
 * `match` is tested against the gap text. The first matching rule wins, so put the
 * specific topics above the general ones. Edit freely: this list is the demo content.
 */
export type ActionPlanSeedRule = {
  match: RegExp;
  /** Department that should own actions of this kind. */
  owner: string;
  /** `{gap}` is replaced with a short summary of what the gap says is missing. */
  template: string;
};

export const ACTION_PLAN_SEED_RULES: ActionPlanSeedRule[] = [
  // Training sits first: a training gap belongs to HR whatever the underlying topic is.
  {
    match: /\b(training|awareness|induction|staff training)\b/i,
    owner: 'Human Resources',
    template:
      'Add a module to the annual AML/CFT training that covers {gap}. Track completion per employee and retain attendance records for the audit trail.',
  },
  {
    match: /\b(sanction|screening|watch ?list|pep|politically exposed)\b/i,
    owner: 'Compliance',
    template:
      'Update the sanctions and PEP screening procedure to cover {gap}. Configure the screening tool accordingly, re-screen the existing customer base, and retain the screening evidence.',
  },
  {
    match: /\b(suspicious|str|sar|reporting|goaml)\b/i,
    owner: 'Compliance',
    template:
      'Extend the suspicious transaction reporting procedure to address {gap}. Define the escalation path and reporting deadline, and evidence the first reporting cycle.',
  },
  {
    match: /\b(record|retention|archive|five year|5 year)\b/i,
    owner: 'Operations',
    template:
      'Amend the record retention schedule so that {gap} is retained for the required period. Migrate existing records and confirm retrieval within the agreed service level.',
  },
  {
    match: /\b(risk assessment|risk based|rba|risk rating)\b/i,
    owner: 'Risk',
    template:
      'Revise the enterprise-wide risk assessment methodology to reflect {gap}. Re-run the assessment, and present the revised risk ratings for approval.',
  },
  {
    match: /\b(cdd|kyc|due diligence|beneficial owner|identification|verification)\b/i,
    owner: 'Compliance',
    template:
      'Update the customer due diligence standard so that {gap} is captured at onboarding and at periodic review. Remediate affected existing customer files.',
  },
  {
    match: /\b(monitoring|transaction|threshold|alert)\b/i,
    owner: 'Operations',
    template:
      'Tune the transaction monitoring rules to cover {gap}. Document the rationale for the thresholds and evidence the post-tuning alert review.',
  },
  {
    match: /\b(governance|board|senior management|mlro|oversight|committee)\b/i,
    owner: 'Compliance',
    template:
      'Document the governance arrangement covering {gap}, obtain board or senior management approval, and record the decision in the committee minutes.',
  },
  {
    match: /\b(audit|independent|testing|assurance)\b/i,
    owner: 'Internal Audit',
    template:
      'Add {gap} to the independent testing plan. Complete the review, log any findings, and track them through to closure.',
  },
];

/** Used when nothing in the catalog matches the gap. */
export const ACTION_PLAN_SEED_FALLBACK: Omit<ActionPlanSeedRule, 'match'> = {
  owner: 'Compliance',
  template:
    'Update the internal policy so that {gap} is explicitly addressed. Assign an owner, obtain approval, and retain evidence that the control is operating.',
};

export type SeededActionPlan = {
  analysisPointId: string;
  gapIndex: number;
  actionPlan: string;
  priority: ActionPlanPriority;
  targetDate: string;
  ownerLabel: string;
};

/** Trim a gap statement down to something that reads well mid-sentence. */
export function summarizeGapForAction(gap: CapGap): string {
  const raw = (gap.missing || gap.fix || '').replace(/\s+/g, ' ').trim();
  if (!raw) return 'the requirement set out in this clause';

  let text = raw
    .replace(/^(no|there is no|the (policy|manual|document) (does not|doesn't))\s+/i, '')
    .replace(/^(equivalent\s+)?internal (procedure|control|policy)\s+(covers|addresses)\s*[—:-]?\s*/i, '')
    .replace(/^missing:\s*/i, '');

  // Keep it to the first sentence so the generated action stays one readable paragraph.
  const cut = text.search(/[.;]/);
  if (cut > 40) text = text.slice(0, cut);

  text = text.trim().replace(/[.;,]$/, '');
  if (!text) return 'the requirement set out in this clause';
  return text.charAt(0).toLowerCase() + text.slice(1);
}

function ruleFor(gapText: string): Omit<ActionPlanSeedRule, 'match'> {
  return ACTION_PLAN_SEED_RULES.find((r) => r.match.test(gapText)) ?? ACTION_PLAN_SEED_FALLBACK;
}

/** First-draft action for one CAP gap, in the voice of the analysis itself. */
export function buildSeededActionPlan(
  analysisPointId: string,
  gap: CapGap,
  from: Date = new Date(),
): SeededActionPlan {
  const gapText = `${gap.missing ?? ''} ${gap.fix ?? ''}`;
  const rule = ruleFor(gapText);
  const priority = normalizeGapRisk(gap.priority);

  return {
    analysisPointId,
    gapIndex: gap.index,
    actionPlan: rule.template.replace('{gap}', summarizeGapForAction(gap)),
    priority,
    targetDate: defaultTargetDateForGapRisk(priority, from),
    ownerLabel: rule.owner,
  };
}
