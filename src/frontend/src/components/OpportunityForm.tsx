import { useState, type FormEvent, type ReactNode } from "react";
import { ApiError } from "../lib/api";
import { forecastCategories, toPayload, type FormValues, type Stage } from "../lib/opportunities";
import { strings } from "../strings";

type Props = {
  mode: "create" | "edit";
  /** From useStages(): the labels shown here are the ones in the database. */
  stages: Stage[];
  accounts: { id: string; name: string }[];
  initial: FormValues;
  /** Sends the body to the API. A rejected promise with an ApiError shows its field errors under the fields. */
  onSubmit: (body: Record<string, unknown>) => Promise<void>;
};

function Field({ id, label, error, children }: { id: string; label: string; error?: string; children: ReactNode }) {
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      {children}
      {error && (
        <p id={`${id}-error`} role="alert" className="field-error">
          {error}
        </p>
      )}
    </div>
  );
}

/** Create or edit an opportunity. Per-field messages come from the API's 422; the stage can be chosen on create only. */
export function OpportunityForm({ mode, stages, accounts, initial, onSubmit }: Props) {
  const text = strings.opportunity;
  const [values, setValues] = useState<FormValues>(initial);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [failure, setFailure] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const input = (field: keyof FormValues) => ({
    id: `opportunity-${field}`,
    value: values[field],
    "aria-describedby": errors[field] ? `opportunity-${field}-error` : undefined,
    "aria-invalid": errors[field] ? true : undefined,
    onChange: (event: { target: { value: string } }) => setValues({ ...values, [field]: event.target.value }),
  });

  async function submit(event: FormEvent) {
    event.preventDefault();
    setSaving(true);
    setErrors({});
    setFailure(null);
    try {
      await onSubmit(toPayload(values, mode));
    } catch (error) {
      const fields = error instanceof ApiError ? (error.problem?.errors ?? []) : [];
      setErrors(Object.fromEntries(fields.map((e) => [e.field.replace(/^amount\.(amount|currency)$/, "amount"), e.message])));
      setFailure(fields.length === 0 ? (error instanceof Error ? error.message : text.saveFailed) : null);
    } finally {
      setSaving(false);
    }
  }

  return (
    <form onSubmit={(event) => void submit(event)} noValidate>
      {failure && <p role="alert">{failure}</p>}
      <Field id="opportunity-name" label={text.name} error={errors.name}>
        <input {...input("name")} required maxLength={200} />
      </Field>
      <Field id="opportunity-accountId" label={text.account} error={errors.accountId}>
        <select {...input("accountId")} required>
          <option value="">{text.choose}</option>
          {accounts.map((account) => (
            <option key={account.id} value={account.id}>
              {account.name}
            </option>
          ))}
        </select>
      </Field>
      <Field id="opportunity-stageKey" label={text.stage} error={errors.stageKey}>
        <select {...input("stageKey")} required disabled={mode === "edit"}>
          {stages
            .filter((stage) => stage.active && (!stage.isTerminal || stage.key === values.stageKey))
            .map((stage) => (
              <option key={stage.key} value={stage.key}>
                {stage.label}
              </option>
            ))}
        </select>
        {mode === "edit" && <p>{text.stageHint}</p>}
      </Field>
      <Field id="opportunity-amount" label={text.amount} error={errors.amount}>
        <input {...input("amount")} inputMode="decimal" />
      </Field>
      <Field id="opportunity-currency" label={text.currency}>
        <input {...input("currency")} maxLength={3} />
      </Field>
      <Field id="opportunity-hourlyRate" label={text.hourlyRate} error={errors.hourlyRate}>
        <input {...input("hourlyRate")} inputMode="decimal" />
      </Field>
      <Field id="opportunity-estHours" label={text.estHours} error={errors.estHours}>
        <input {...input("estHours")} inputMode="decimal" />
      </Field>
      <Field id="opportunity-expectedCloseDate" label={text.expectedCloseDate} error={errors.expectedCloseDate}>
        <input {...input("expectedCloseDate")} type="date" />
      </Field>
      <Field id="opportunity-nextStep" label={text.nextStep} error={errors.nextStep}>
        <input {...input("nextStep")} maxLength={200} />
      </Field>
      <Field id="opportunity-nextStepDate" label={text.nextStepDate} error={errors.nextStepDate}>
        <input {...input("nextStepDate")} type="date" />
      </Field>
      <Field id="opportunity-forecastCategory" label={text.forecastCategory} error={errors.forecastCategory}>
        <select {...input("forecastCategory")}>
          <option value="">{text.none}</option>
          {forecastCategories.map((category) => (
            <option key={category} value={category}>
              {text.forecast[category]}
            </option>
          ))}
        </select>
      </Field>
      <button type="submit" disabled={saving}>
        {saving ? text.saving : text.save}
      </button>
    </form>
  );
}
