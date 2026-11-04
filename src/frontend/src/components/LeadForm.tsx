import { useState, type FormEvent } from "react";
import { ApiError } from "../lib/api";
import { emptyLead, fieldErrors, requiredFields, saveLead, toFormValues, type Lead, type LeadFormValues } from "../lib/leads";
import { strings } from "../strings";

type Props = {
  /** The active lead sources to choose from. */
  sources: { key: string; label: string }[];
  /** The lead to edit with the ETag it was read with; leave out to create a lead. */
  existing?: { lead: Lead; etag: string };
  onSaved: (lead: Lead, etag: string | null) => void;
};

const textFields = ["firstName", "lastName", "companyName", "jobTitle", "phone", "mobile", "email", "website", "countryCode", "nextStep"] as const;

/** Create or edit a lead. The API validates; its message is shown next to the field it names (no browser validation pop-ups). */
export function LeadForm({ sources, existing, onSaved }: Props) {
  const [values, setValues] = useState<LeadFormValues>(existing ? toFormValues(existing.lead) : emptyLead);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [message, setMessage] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const set = (name: keyof LeadFormValues, value: string) => setValues((current) => ({ ...current, [name]: value }));

  async function submit(event: FormEvent) {
    event.preventDefault();
    setSaving(true);
    setErrors({});
    setMessage(null);
    try {
      const saved = await saveLead(values, existing && { id: existing.lead.id, etag: existing.etag });
      onSaved(saved.lead, saved.etag);
    } catch (error) {
      const failing = fieldErrors(error);
      setErrors(failing);
      if (Object.keys(failing).length > 0) {
        setMessage(strings.leads.fixFields);
      } else {
        setMessage(error instanceof ApiError ? error.message : strings.leads.saveFailed);
      }
    } finally {
      setSaving(false);
    }
  }

  const describedBy = (name: string) => (errors[name] ? `lead-${name}-error` : undefined);
  const label = (name: keyof LeadFormValues) => strings.leads.fields[name] + (requiredFields.includes(name) ? " *" : "");

  return (
    <form onSubmit={(event) => void submit(event)} noValidate>
      {message && <p role="alert">{message}</p>}
      <p>{strings.leads.requiredHint}</p>
      {textFields.map((name) => (
        <div key={name}>
          <label htmlFor={`lead-${name}`}>{label(name)}</label>
          <input
            id={`lead-${name}`}
            name={name}
            value={values[name]}
            onChange={(event) => set(name, event.target.value)}
            aria-required={requiredFields.includes(name)}
            aria-invalid={errors[name] ? true : undefined}
            aria-describedby={describedBy(name)}
          />
          {errors[name] && (
            <p id={`lead-${name}-error`} role="alert">
              {errors[name]}
            </p>
          )}
        </div>
      ))}
      <div>
        <label htmlFor="lead-leadSource">{label("leadSource")}</label>
        <select
          id="lead-leadSource"
          name="leadSource"
          value={values.leadSource}
          onChange={(event) => set("leadSource", event.target.value)}
          aria-required
          aria-invalid={errors.leadSource ? true : undefined}
          aria-describedby={describedBy("leadSource")}
        >
          <option value="">{strings.leads.chooseSource}</option>
          {sources.map((source) => (
            <option key={source.key} value={source.key}>
              {source.label}
            </option>
          ))}
        </select>
        {errors.leadSource && (
          <p id="lead-leadSource-error" role="alert">
            {errors.leadSource}
          </p>
        )}
      </div>
      <button type="submit" disabled={saving}>
        {saving ? strings.leads.saving : strings.leads.save}
      </button>
    </form>
  );
}
