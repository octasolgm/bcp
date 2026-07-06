import { REFERENCE_FIELD_REGEX } from './reference-map-prompt';

export type ReferenceComplianceBlock = {
  title: string;
  body: string;
  referencePdf: string;
  outputResponse: string;
  fulfilledClauses: string;
  status: string;
  confidence: string;
  correctiveAction: string;
  responsibility: string;
  fields: { label: string; value: string }[];
};

function parseComplianceBlock(block: string): {
  title: string;
  body: string;
  fields: { label: string; value: string }[];
} {
  const lines = block.split('\n');
  const fields: { label: string; value: string }[] = [];
  const headerLines: string[] = [];
  let currentField: { label: string; valueLines: string[] } | null = null;

  for (const line of lines) {
    const match = line.match(REFERENCE_FIELD_REGEX);
    if (match) {
      if (currentField) {
        fields.push({
          label: currentField.label,
          value: currentField.valueLines.join('\n').trim(),
        });
      }
      currentField = {
        label: match[1],
        valueLines: match[2] ? [match[2]] : [],
      };
    } else if (currentField) {
      currentField.valueLines.push(line);
    } else {
      headerLines.push(line);
    }
  }

  if (currentField) {
    fields.push({
      label: currentField.label,
      value: currentField.valueLines.join('\n').trim(),
    });
  }

  const nonEmptyHeader = headerLines.filter((l) => l.trim());
  return {
    title: nonEmptyHeader[0]?.trim() ?? '',
    body: nonEmptyHeader.slice(1).join('\n').trim(),
    fields,
  };
}

function fieldValue(
  fields: { label: string; value: string }[],
  label: string,
): string {
  return fields.find((f) => f.label === label)?.value?.trim() ?? '';
}

/** Parse plain-text compliance block from Landing AI or Gemini Phase 2 output */
export function parseReferenceComplianceBlock(message: string): ReferenceComplianceBlock {
  const trimmed = message.trim();
  const parsed = parseComplianceBlock(trimmed);
  return {
    title: parsed.title,
    body: parsed.body,
    referencePdf: fieldValue(parsed.fields, 'Reference PDF'),
    outputResponse: fieldValue(parsed.fields, 'Output/Response'),
    fulfilledClauses: fieldValue(parsed.fields, 'Fulfilled clauses'),
    status: fieldValue(parsed.fields, 'Comply Yes/No (Status)'),
    confidence: fieldValue(parsed.fields, 'Compliance Confidence %'),
    correctiveAction: fieldValue(parsed.fields, 'Corrective Action Plan'),
    responsibility: fieldValue(parsed.fields, 'Responsibility'),
    fields: parsed.fields,
  };
}
