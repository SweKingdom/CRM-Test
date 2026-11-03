import { describe, expect, it } from "vitest";
import { formatDate, formatDateTime } from "./format";

describe("AC 6: shared date formatter", () => {
  it("formats a moment as an ISO date and a Stockholm time", () => {
    expect(formatDate("2026-11-02T13:30:00Z")).toBe("2026-11-02");
    expect(formatDateTime("2026-11-02T13:30:00Z")).toBe("2026-11-02 14:30");
  });

  it("uses summer time (UTC+2) in summer", () => {
    expect(formatDateTime("2026-07-01T10:05:00Z")).toBe("2026-07-01 12:05");
  });

  it("moves the date to the Stockholm day when the moment is after midnight there", () => {
    expect(formatDate("2026-11-02T23:30:00Z")).toBe("2026-11-03");
    expect(formatDateTime(new Date("2026-11-02T23:30:00Z"))).toBe("2026-11-03 00:30");
  });

  it("never converts a plain date such as a work date", () => {
    expect(formatDate("2026-11-02")).toBe("2026-11-02");
  });

  it("returns an empty string for an invalid value", () => {
    expect(formatDate("not a date")).toBe("");
    expect(formatDateTime("")).toBe("");
  });
});
