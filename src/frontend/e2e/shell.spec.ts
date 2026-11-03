import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";

// US-148. The API is mocked with page.route, so these specs need only the Vite dev server; the sign-in smoke test at the
// end needs the whole stack (docker compose up, API on :8080) and the credentials of a seeded Keycloak user in the
// environment: ZEPHYR_E2E_USER and ZEPHYR_E2E_PASSWORD.

const allRoutes = ["/accounts", "/contacts", "/leads", "/opportunities", "/projects"];
const searchUrl = /\/api\/v1\/search\?/;

const account = (n: number) => ({ type: "account", id: `a-${n}`, title: `Acme ${n} AB`, snippet: "Stockholm", score: 1 });
const hits = [
  ...[1, 2, 3, 4, 5, 6, 7].map(account),
  { type: "contact", id: "c-1", title: "Acme Contact", snippet: "acme@example.test", score: 1 },
  { type: "lead", id: "l-1", title: "Acme lead", snippet: "New", score: 1 },
];

async function signIn(page: Page, routes = allRoutes) {
  await page.route("**/api/v1/me/permissions", (route) => route.fulfill({ json: { roles: ["sales"], permissions: [], routes } }));
  await page.route("**/auth/me", (route) => route.fulfill({ json: { id: "u-1", name: "Test User" } }));
}

/** Answers every search with `items`, and records the requested URLs. */
async function mockSearch(page: Page, items: unknown[] = hits) {
  const urls: string[] = [];
  await page.route(searchUrl, (route) => {
    urls.push(route.request().url());
    return route.fulfill({ json: { items, fallback: false } });
  });
  return urls;
}

const searchBox = (page: Page) => page.getByRole("combobox", { name: "Search" });

/** Opens a navigation link with the keyboard: the open dropdown may cover the link, and that must not stop anyone. */
async function openNavLink(page: Page, name: string) {
  const link = page.getByRole("navigation", { name: "Main" }).getByRole("link", { name });
  await link.focus();
  await link.press("Enter");
}

test("AC 1: signed-out users are sent to sign-in", async ({ page }) => {
  await page.route("**/api/v1/me/permissions", (route) => route.fulfill({ status: 401, json: { title: "Unauthorized" } }));
  await page.route("**/auth/login**", (route) => route.fulfill({ contentType: "text/html", body: "<p>sign-in page</p>" }));
  await page.goto("/accounts");
  await expect(page).toHaveURL(/\/auth\/login\?returnUrl=%2Faccounts$/);
});

test("AC 1: the shell shows header, search and only the navigation items the user may use", async ({ page }) => {
  await signIn(page, ["/accounts", "/time"]);
  await page.goto("/");
  await expect(page.getByRole("banner")).toBeVisible();
  await expect(searchBox(page)).toBeVisible();
  const nav = page.getByRole("navigation", { name: "Main" });
  await expect(nav.getByRole("link")).toHaveText(["Accounts", "Time"]);
  await expect(page.getByText("Test User")).toBeVisible();
});

test("AC 1: a page the user may not open shows access denied inside the shell", async ({ page }) => {
  await signIn(page, ["/accounts"]);
  await page.goto("/leads");
  await expect(page.getByRole("heading", { name: "Access denied" })).toBeVisible();
  await expect(searchBox(page)).toBeVisible();
});

test("AC 2: typing 2 characters searches once (debounced) and groups results, at most 5 per type", async ({ page }) => {
  await signIn(page);
  const urls = await mockSearch(page);
  await page.goto("/");
  await searchBox(page).pressSequentially("a");
  await page.waitForTimeout(400);
  expect(urls).toHaveLength(0);
  await expect(page.getByRole("listbox")).toBeHidden();

  await searchBox(page).clear();
  await searchBox(page).pressSequentially("acme");
  const listbox = page.getByRole("listbox", { name: "Search results" });
  await expect(listbox).toBeVisible();
  expect(urls).toHaveLength(1);
  expect(urls[0]).toContain("q=acme");
  await expect(listbox.getByRole("group", { name: "Accounts" }).getByRole("option")).toHaveCount(5);
  await expect(listbox.getByRole("group", { name: "Contacts" }).getByRole("option")).toHaveCount(1);
  await expect(listbox.getByRole("group", { name: "Leads" }).getByRole("option")).toHaveCount(1);
});

test("AC 2: arrow keys move and Enter on a highlighted account opens Account 360", async ({ page }) => {
  await signIn(page);
  await mockSearch(page);
  await page.goto("/");
  await searchBox(page).pressSequentially("acme");
  await expect(page.getByRole("option")).toHaveCount(7);
  await searchBox(page).press("ArrowDown");
  await searchBox(page).press("ArrowDown");
  await expect(page.getByRole("option", { name: /Acme 2 AB/ })).toHaveAttribute("aria-selected", "true");
  await searchBox(page).press("ArrowUp");
  await expect(page.getByRole("option", { name: /Acme 1 AB/ })).toHaveAttribute("aria-selected", "true");
  await searchBox(page).press("Enter");
  await expect(page).toHaveURL(/\/accounts\/a-1$/);
});

test("AC 2: clicking a contact opens Contact 360", async ({ page }) => {
  await signIn(page);
  await mockSearch(page);
  await page.goto("/");
  await searchBox(page).pressSequentially("acme");
  await page.getByRole("option", { name: /Acme Contact/ }).click();
  await expect(page).toHaveURL(/\/contacts\/c-1$/);
});

test("AC 2: Enter with nothing highlighted opens /search?q= with every result grouped by type", async ({ page }) => {
  await signIn(page);
  await mockSearch(page);
  await page.goto("/");
  await searchBox(page).pressSequentially("acme");
  await expect(page.getByRole("listbox")).toBeVisible();
  await searchBox(page).press("Enter");
  await expect(page).toHaveURL(/\/search\?q=acme$/);
  await expect(page.getByRole("heading", { name: "Search results for “acme”" })).toBeVisible();
  await expect(page.getByRole("heading", { level: 2 })).toHaveText(["Accounts", "Contacts", "Leads"]);
  await expect(page.getByRole("main").getByRole("link", { name: /Acme \d AB/ })).toHaveCount(7);
});

test("AC 3: no hits shows the empty message and does not block navigation", async ({ page }) => {
  await signIn(page);
  await mockSearch(page, []);
  await page.goto("/");
  await searchBox(page).pressSequentially("zzzz");
  await expect(page.getByText("No results for “zzzz”. Check the spelling or try fewer letters.").first()).toBeVisible();
  await openNavLink(page, "Accounts");
  await expect(page).toHaveURL(/\/accounts$/);
});

test("AC 3: an API error shows the unavailable message with a retry control that works", async ({ page }) => {
  await signIn(page);
  await page.route(searchUrl, (route) => route.fulfill({ status: 500, contentType: "application/problem+json", json: { title: "Server error", status: 500 } }));
  await page.goto("/");
  await searchBox(page).pressSequentially("acme");
  await expect(page.getByText("Search is unavailable right now. Try again.").first()).toBeVisible();

  await mockSearch(page);
  await page.getByRole("button", { name: "Try again" }).click();
  await expect(page.getByRole("option")).toHaveCount(7);

  await openNavLink(page, "Contacts");
  await expect(page).toHaveURL(/\/contacts$/);
});

test("AC 4: / focuses the search, Esc closes it, and the combobox exposes its state", async ({ page }) => {
  await signIn(page);
  await mockSearch(page);
  await page.goto("/");
  await page.getByRole("heading", { level: 1 }).click();
  await page.keyboard.press("/");
  await expect(searchBox(page)).toBeFocused();
  await expect(searchBox(page)).toHaveAttribute("aria-expanded", "false");

  await searchBox(page).pressSequentially("acme");
  await expect(searchBox(page)).toHaveAttribute("aria-expanded", "true");
  await expect(page.getByRole("status").filter({ hasText: "results available" })).toHaveText(/7 results available/);
  await searchBox(page).press("ArrowDown");
  const option = page.getByRole("option").first();
  await expect(searchBox(page)).toHaveAttribute("aria-activedescendant", (await option.getAttribute("id")) ?? "");

  await searchBox(page).press("Escape");
  await expect(searchBox(page)).toHaveAttribute("aria-expanded", "false");
  await expect(page.getByRole("listbox")).toBeHidden();
});

test("AC 4: axe finds no serious violations with the dropdown open, with the empty message, and on the results page", async ({ page }) => {
  await signIn(page);
  await mockSearch(page);
  const serious = async () => {
    const result = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
    return result.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  };
  await page.goto("/");
  await searchBox(page).pressSequentially("acme");
  await searchBox(page).press("ArrowDown");
  await expect(page.getByRole("listbox")).toBeVisible();
  expect(await serious()).toEqual([]);

  await page.goto("/search?q=acme");
  await expect(page.getByRole("heading", { name: /Search results/ })).toBeVisible();
  await expect(page.getByRole("heading", { level: 2 })).toHaveCount(3);
  expect(await serious()).toEqual([]);

  await mockSearch(page, []);
  await searchBox(page).pressSequentially("zzzz");
  await expect(page.getByText("No results for “zzzz”", { exact: false }).first()).toBeVisible();
  expect(await serious()).toEqual([]);
});

test("AC 5: at 360 px there is no horizontal scroll, the navigation sits behind a menu button and search stays reachable", async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 740 });
  await signIn(page);
  await mockSearch(page);
  await page.goto("/");
  const scrolls = () => page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
  expect(await scrolls()).toBe(false);

  const nav = page.getByRole("navigation", { name: "Main" });
  await expect(nav).toBeHidden();
  await page.getByRole("button", { name: "Menu" }).click();
  await expect(nav.getByRole("link", { name: "Accounts" })).toBeVisible();
  expect(await scrolls()).toBe(false);
  await nav.getByRole("link", { name: "Accounts" }).click();
  await expect(nav).toBeHidden();

  await searchBox(page).pressSequentially("acme");
  await expect(page.getByRole("listbox")).toBeVisible();
  expect(await scrolls()).toBe(false);
});

test("smoke: real Keycloak sign-in lands in the shell", async ({ page }) => {
  const user = process.env.ZEPHYR_E2E_USER;
  const password = process.env.ZEPHYR_E2E_PASSWORD;
  test.skip(!user || !password, "needs the running stack and ZEPHYR_E2E_USER / ZEPHYR_E2E_PASSWORD");
  await page.goto("/");
  await page.locator("#username").fill(user ?? "");
  await page.locator("#password").fill(password ?? "");
  await page.locator("#kc-login").click();
  await expect(page.getByRole("banner")).toBeVisible();
  await expect(searchBox(page)).toBeVisible();
});
