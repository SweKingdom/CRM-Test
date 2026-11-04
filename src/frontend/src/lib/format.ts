// The one place dates and times are formatted. Display time zone: Europe/Stockholm for every user (PO answer #80: a per-user
// time zone setting comes later). Dates read 2026-11-02, times 14:30.
const zone = "Europe/Stockholm";
const plainDate = /^\d{4}-\d{2}-\d{2}$/;

const parts = new Intl.DateTimeFormat("en-GB", {
  timeZone: zone,
  year: "numeric",
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
  hourCycle: "h23",
});

function read(value: string | Date): Record<string, string> | null {
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) {
    return null;
  }
  return Object.fromEntries(parts.formatToParts(date).map((part) => [part.type, part.value]));
}

/** `2026-11-02`. A plain date such as a work date is returned as it is: it is a calendar day, never converted. A moment is shown as the Stockholm day. Empty for an invalid value. */
export function formatDate(value: string | Date): string {
  if (typeof value === "string" && plainDate.test(value)) {
    return value;
  }
  const p = read(value);
  return p ? `${p.year}-${p.month}-${p.day}` : "";
}

/** `2026-11-02 14:30` in Europe/Stockholm. Empty for an invalid value. */
export function formatDateTime(value: string | Date): string {
  const p = read(value);
  return p ? `${p.year}-${p.month}-${p.day} ${p.hour}:${p.minute}` : "";
}
