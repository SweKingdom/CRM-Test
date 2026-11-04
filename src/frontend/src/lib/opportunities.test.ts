import { describe, expect, it } from "vitest";
import { stageLabel, toPayload, type FormValues, type Stage } from "./opportunities";

const empty: FormValues = {
  name: " Big deal ",
  accountId: "a1",
  stageKey: "discover",
  amount: "",
  currency: "SEK",
  hourlyRate: "",
  estHours: "",
  nextStep: "",
  nextStepDate: "",
  expectedCloseDate: "",
  forecastCategory: "",
};

const stages: Stage[] = [{ key: "qualify", label: "Qualification", sortOrder: 2, isTerminal: false, outcome: null, active: true }];

describe("US-156 AC 2: stage labels come from the stage list", () => {
  it("shows the label the API returned, so a renamed label needs no deploy", () => {
    expect(stageLabel(stages, "qualify")).toBe("Qualification");
  });

  it("falls back to the key while the list is loading or the stage is unknown", () => {
    expect(stageLabel(undefined, "qualify")).toBe("qualify");
    expect(stageLabel(stages, "gone")).toBe("gone");
  });
});

describe("US-156: the form payload", () => {
  it("sends the stage and leaves empty optional fields out on create", () => {
    expect(toPayload(empty, "create")).toEqual({ name: "Big deal", accountId: "a1", stageKey: "discover" });
  });

  it("never sends the stage on edit and sends null for cleared fields", () => {
    const body = toPayload({ ...empty, nextStep: "Call", amount: "100.00", currency: "EUR" }, "edit");
    expect(body).not.toHaveProperty("stageKey");
    expect(body).toMatchObject({ nextStep: "Call", amount: { amount: "100.00", currency: "EUR" }, hourlyRate: null, expectedCloseDate: null });
  });

  it("sends an amount as text with its currency", () => {
    expect(toPayload({ ...empty, amount: " 1000.50 ", currency: "EUR" }, "create").amount).toEqual({ amount: "1000.50", currency: "EUR" });
  });
});
