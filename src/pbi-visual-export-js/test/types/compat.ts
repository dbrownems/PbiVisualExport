// Compile-time check (tsconfig.check.json): real powerbi-client objects fit the
// exporter's structural types.
import type { Page, Report, VisualDescriptor } from "powerbi-client";
import type {} from "powerbi-report-authoring";
import type { PbiPage, PbiReport, PbiVisual } from "../../src/types";

declare const report: Report;
declare const page: Page;
declare const visual: VisualDescriptor;

export const r: PbiReport = report;
export const p: PbiPage = page;
export const v: PbiVisual = visual;
