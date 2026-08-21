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
  /**
   * The verification step that follows the fix. Seeded alongside the main action on
   * gaps that carry real risk, so those gaps arrive with the pair of actions a
   * remediation normally needs: change the control, then prove it works.
   */
  followUp?: { owner: string; template: string };
};

export const ACTION_PLAN_SEED_RULES: ActionPlanSeedRule[] = [
  // Training sits first: a training gap belongs to HR whatever the underlying topic is.
  {
    match: /\b(training|awareness|induction|staff training)\b/i,
    owner: 'Human Resources',
    template:
      'Add a module to the annual AML/CFT training that covers {gap}. Track completion per employee and retain attendance records for the audit trail.',
    followUp: {
      owner: 'Compliance',
      template:
        'Assess training effectiveness on {gap} through a post-training test, and re-run the module for anyone who does not pass.',
    },
  },
  {
    match: /\b(sanction|screening|watch ?list|pep|politically exposed)\b/i,
    owner: 'Compliance',
    template:
      'Update the sanctions and PEP screening procedure to cover {gap}. Configure the screening tool accordingly, re-screen the existing customer base, and retain the screening evidence.',
    followUp: {
      owner: 'Internal Audit',
      template:
        'Sample-test the screening output covering {gap} once the updated procedure is live, and report the results to the MLRO.',
    },
  },
  {
    match: /\b(suspicious|str|sar|reporting|goaml)\b/i,
    owner: 'Compliance',
    template:
      'Extend the suspicious transaction reporting procedure to address {gap}. Define the escalation path and reporting deadline, and evidence the first reporting cycle.',
    followUp: {
      owner: 'Internal Audit',
      template:
        'Review a sample of reports raised after the change to confirm {gap} is being handled within the stated deadline.',
    },
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
    followUp: {
      owner: 'Compliance',
      template:
        'Feed the revised ratings covering {gap} into the customer risk model, and re-rate the affected portfolio.',
    },
  },
  {
    match: /\b(cdd|kyc|due diligence|beneficial owner|identification|verification)\b/i,
    owner: 'Compliance',
    template:
      'Update the customer due diligence standard so that {gap} is captured at onboarding and at periodic review. Remediate affected existing customer files.',
    followUp: {
      owner: 'Operations',
      template:
        'Run a file-remediation sweep so that existing customer records carry {gap}, and report the completion rate monthly until it closes.',
    },
  },
  {
    match: /\b(monitoring|transaction|threshold|alert)\b/i,
    owner: 'Operations',
    template:
      'Tune the transaction monitoring rules to cover {gap}. Document the rationale for the thresholds and evidence the post-tuning alert review.',
    followUp: {
      owner: 'Risk',
      template:
        'Validate the tuned thresholds behind {gap} against a back-test, and record the false-positive rate before and after.',
    },
  },
  {
    match: /\b(governance|board|senior management|mlro|oversight|committee)\b/i,
    owner: 'Compliance',
    template:
      'Document the governance arrangement covering {gap}, obtain board or senior management approval, and record the decision in the committee minutes.',
    followUp: {
      owner: 'Compliance',
      template:
        'Add {gap} to the standing committee agenda so the arrangement is reviewed at the agreed frequency.',
    },
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
  followUp: {
    owner: 'Internal Audit',
    template:
      'Confirm through independent testing that {gap} is addressed in practice, and log any residual finding.',
  },
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
  const rule = ruleFor(`${gap.missing ?? ''} ${gap.fix ?? ''}`);
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

/**
 * The draft actions a gap starts life with: the fix, plus a verification step when the
 * gap carries enough risk to warrant one. Low-risk gaps get the single action, so the
 * report does not open with busywork attached to minor findings.
 */
export function buildSeededActionPlansForGap(
  analysisPointId: string,
  gap: CapGap,
  from: Date = new Date(),
): SeededActionPlan[] {
  const primary = buildSeededActionPlan(analysisPointId, gap, from);
  const rule = ruleFor(`${gap.missing ?? ''} ${gap.fix ?? ''}`);
  if (!rule.followUp || primary.priority === 'low') return [primary];

  return [
    primary,
    {
      analysisPointId,
      gapIndex: gap.index,
      actionPlan: rule.followUp.template.replace('{gap}', summarizeGapForAction(gap)),
      priority: primary.priority,
      // The check follows the fix, so it lands one cycle later.
      targetDate: defaultTargetDateForGapRisk(primary.priority, addDays(from, 15)),
      ownerLabel: rule.followUp.owner,
    },
  ];
}

function addDays(from: Date, days: number): Date {
  const next = new Date(from);
  next.setDate(next.getDate() + days);
  return next;
}
