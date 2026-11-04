import { describe, expect, it } from "vitest";
import { ApiError } from "./api";
import { emptyLead, fieldErrors, toBody, toFormValues, type Lead } from "./leads";

describe("US-153: lead form helpers", () => {
  it("AC 2: sends blank optional fields as null and keeps blank required fields as typed, so the API names the field", () => {
    const body = toBody({ ...emptyLead, lastName: " Berg ", companyName: "Berg Demo AB" });
    expect(body.lastName).toBe("Berg");
    expect(body.leadSource).toBe("");
    expect(body.firstName).toBeNull();
    expect(body.countryCode).toBeNull();
  });

  it("shows a stored lead in the form without nulls", () => {
    const lead = { id: "1", lastName: "Berg", companyName: "Berg Demo AB", leadSource: "web", firstName: null, countryCode: "SE" } as Lead;
    const values = toFormValues(lead);
    expect(values.firstName).toBe("");
    expect(values.countryCode).toBe("SE");
  });

  it("AC 1: maps the API's field errors to messages by field name", () => {
    const error = new ApiError(422, {
      errors: [
        { field: "leadSource", code: "REQUIRED", message: "leadSource is required." },
        { field: "leadSource", code: "INVALID", message: "second message" },
        { field: "email", code: "INVALID", message: "email must look like name@example.com." },
      ],
    });
    expect(fieldErrors(error)).toEqual({ leadSource: "leadSource is required.", email: "email must look like name@example.com." });
  });

  it("has no field errors for another kind of error", () => {
    expect(fieldErrors(new ApiError(412))).toEqual({});
    expect(fieldErrors(new Error("network"))).toEqual({});
  });
});
