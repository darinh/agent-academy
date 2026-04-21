using System.Text.Json;
using AgentAcademy.Forge.Models;

namespace AgentAcademy.Forge.Execution;

/// <summary>
/// Frozen catalog of seeded-defect test cases for measuring fidelity detection accuracy.
/// Each case pairs a valid source-intent artifact with a drifted output artifact
/// and declares the expected fidelity verdict (ground truth).
///
/// Artifacts are structurally valid for fidelity benchmarking only — the implementation
/// artifacts use placeholder component IDs since cross-artifact resolution against
/// function_design is not what we're testing.
/// </summary>
public static class SeededDefectCatalog
{
    /// <summary>
    /// SD-OMIT: Source requires per-user AND per-IP rate limiting.
    /// Output drops per-IP entirely. Blocking drift.
    /// </summary>
    public static SeededDefect OmittedConstraint { get; } = new()
    {
        Id = "SD-OMIT",
        Description = "Per-IP rate limiting constraint dropped from output",
        DriftCategory = "blocking",
        ExpectedOverallMatch = "FAIL",
        ExpectedDriftCodes = ["OMITTED_CONSTRAINT"],
        SourceIntent = MakeSourceIntent(
            taskBrief: "Build a rate limiter that throttles requests per-user AND per-IP address. " +
                       "Both dimensions must be enforced simultaneously — a request is rejected only " +
                       "if either the user limit or the IP limit is exceeded.",
            acceptanceCriteria:
            [
                ("AC1", "Requests are throttled per authenticated user ID", true),
                ("AC2", "Requests are throttled per source IP address", true),
                ("AC3", "A request exceeding either limit receives HTTP 429", true)
            ],
            explicitConstraints:
            [
                ("EC1", "Both per-user and per-IP limits must be enforced simultaneously"),
                ("EC2", "Limits are configurable via environment variables")
            ]),
        DriftedOutput = MakeImplementation(
            """
            // rate-limiter.ts — Per-user rate limiting only
            import { RateLimiter } from './core';

            const userLimiter = new RateLimiter({ windowMs: 60000, maxRequests: 100 });

            export function checkRateLimit(userId: string): boolean {
                return userLimiter.check(userId);
            }

            // Note: IP-based rate limiting was considered but omitted
            // for simplicity. Only user-based throttling is implemented.
            """,
            "npm run build",
            "Per-user rate limiter. Uses sliding window algorithm with configurable limits.")
    };

    /// <summary>
    /// SD-INVENT: Source asks for a simple CLI calculator.
    /// Output adds a web UI with charts — never requested.
    /// </summary>
    public static SeededDefect InventedRequirement { get; } = new()
    {
        Id = "SD-INVENT",
        Description = "Output adds web dashboard with charts not in source intent",
        DriftCategory = "advisory",
        ExpectedOverallMatch = "PARTIAL",
        ExpectedDriftCodes = ["INVENTED_REQUIREMENT"],
        SourceIntent = MakeSourceIntent(
            taskBrief: "Build a command-line calculator that evaluates arithmetic expressions. " +
                       "It should support +, -, *, / operators and parentheses. " +
                       "Input comes from stdin, output goes to stdout.",
            acceptanceCriteria:
            [
                ("AC1", "Evaluates arithmetic expressions with +, -, *, / operators", true),
                ("AC2", "Supports parenthesized sub-expressions", true),
                ("AC3", "Reads from stdin and writes results to stdout", true)
            ],
            explicitConstraints:
            [
                ("EC1", "Command-line interface only — no GUI")
            ]),
        DriftedOutput = MakeImplementation(
            """
            // calculator.ts — CLI calculator with bonus web dashboard
            import * as readline from 'readline';
            import express from 'express';
            import { Chart } from 'chart.js';

            // Core calculator (as requested)
            export function evaluate(expr: string): number {
                return Function(`"use strict"; return (${expr})`)();
            }

            // CLI interface (as requested)
            const rl = readline.createInterface({ input: process.stdin });
            rl.on('line', (line) => console.log(evaluate(line)));

            // Web dashboard with history charts (NOT requested)
            const app = express();
            const history: { expr: string; result: number; time: Date }[] = [];

            app.get('/dashboard', (req, res) => {
                res.send(renderDashboard(history));
            });

            app.listen(3000, () => console.log('Dashboard at http://localhost:3000/dashboard'));

            function renderDashboard(h: typeof history): string {
                return `<html><body><canvas id="chart"></canvas>
                    <script>new Chart('chart', { data: ${JSON.stringify(h)} })</script></body></html>`;
            }
            """,
            "npm run build",
            "CLI calculator with arithmetic expression evaluation. Also includes a web dashboard " +
            "for tracking calculation history with charts — useful for analytics.")
    };

    /// <summary>
    /// SD-BROAD: Source asks for a Markdown-to-HTML converter.
    /// Output adds PDF export, syntax highlighting, and LaTeX math rendering.
    /// </summary>
    public static SeededDefect ScopeBroadened { get; } = new()
    {
        Id = "SD-BROAD",
        Description = "Output adds PDF export, syntax highlighting, LaTeX rendering beyond scope",
        DriftCategory = "advisory",
        ExpectedOverallMatch = "PARTIAL",
        ExpectedDriftCodes = ["SCOPE_BROADENED"],
        SourceIntent = MakeSourceIntent(
            taskBrief: "Build a Markdown-to-HTML converter library. It should handle headings, " +
                       "paragraphs, bold, italic, links, images, and code blocks. " +
                       "Output valid HTML5.",
            acceptanceCriteria:
            [
                ("AC1", "Converts Markdown headings (h1-h6) to HTML heading tags", true),
                ("AC2", "Handles bold, italic, links, images, and inline code", true),
                ("AC3", "Converts fenced code blocks to <pre><code> elements", true),
                ("AC4", "Output is valid HTML5", true)
            ],
            explicitConstraints: []),
        DriftedOutput = MakeImplementation(
            """
            // markdown-converter.ts — Full-featured document processing suite
            import { marked } from 'marked';
            import hljs from 'highlight.js';
            import puppeteer from 'puppeteer';
            import katex from 'katex';

            // Core Markdown→HTML (as requested)
            export function toHtml(markdown: string): string {
                return marked(markdown, { gfm: true });
            }

            // Syntax highlighting for code blocks (NOT requested)
            export function toHtmlWithHighlighting(markdown: string): string {
                return marked(markdown, {
                    highlight: (code, lang) => hljs.highlight(code, { language: lang }).value
                });
            }

            // PDF export (NOT requested)
            export async function toPdf(markdown: string, outputPath: string): Promise<void> {
                const html = toHtml(markdown);
                const browser = await puppeteer.launch();
                const page = await browser.newPage();
                await page.setContent(html);
                await page.pdf({ path: outputPath, format: 'A4' });
                await browser.close();
            }

            // LaTeX math rendering (NOT requested)
            export function toHtmlWithMath(markdown: string): string {
                return toHtml(markdown).replace(/\$\$(.*?)\$\$/g, (_, tex) =>
                    katex.renderToString(tex, { displayMode: true }));
            }
            """,
            "npm run build",
            "Comprehensive document processing library. Converts Markdown to HTML with " +
            "syntax highlighting, PDF export via Puppeteer, and LaTeX math rendering via KaTeX.")
    };

    /// <summary>
    /// SD-NARROW: Source asks for full HTTP client with auth, caching, retries.
    /// Output only implements basic requests and retries — caching and auth omitted.
    /// All stated ACs are technically satisfied (ACs are written for core functionality).
    /// </summary>
    public static SeededDefect ScopeNarrowed { get; } = new()
    {
        Id = "SD-NARROW",
        Description = "Output omits auth and caching from full-featured HTTP client",
        DriftCategory = "advisory",
        ExpectedOverallMatch = "PARTIAL",
        ExpectedDriftCodes = ["SCOPE_NARROWED"],
        SourceIntent = MakeSourceIntent(
            taskBrief: "Build a full-featured HTTP client library with request/response handling, " +
                       "automatic retries with exponential backoff, response caching with TTL, " +
                       "and pluggable authentication (API key, Bearer token, OAuth2).",
            acceptanceCriteria:
            [
                ("AC1", "Can make HTTP GET, POST, PUT, DELETE requests", true),
                ("AC2", "Automatically retries failed requests with exponential backoff", true),
                ("AC3", "Supports configurable timeout per request", true)
            ],
            explicitConstraints: []),
        DriftedOutput = MakeImplementation(
            """
            // http-client.ts — Basic HTTP client with retries
            export class HttpClient {
                private timeout: number;
                private maxRetries: number;

                constructor(opts: { timeout?: number; maxRetries?: number } = {}) {
                    this.timeout = opts.timeout ?? 30000;
                    this.maxRetries = opts.maxRetries ?? 3;
                }

                async get(url: string): Promise<Response> { return this.request('GET', url); }
                async post(url: string, body: any): Promise<Response> { return this.request('POST', url, body); }
                async put(url: string, body: any): Promise<Response> { return this.request('PUT', url, body); }
                async delete(url: string): Promise<Response> { return this.request('DELETE', url); }

                private async request(method: string, url: string, body?: any): Promise<Response> {
                    for (let attempt = 0; attempt <= this.maxRetries; attempt++) {
                        try {
                            const controller = new AbortController();
                            setTimeout(() => controller.abort(), this.timeout);
                            return await fetch(url, { method, body: JSON.stringify(body), signal: controller.signal });
                        } catch (err) {
                            if (attempt === this.maxRetries) throw err;
                            await new Promise(r => setTimeout(r, Math.pow(2, attempt) * 1000));
                        }
                    }
                    throw new Error('Unreachable');
                }
            }
            // Note: Caching and authentication features are not yet implemented.
            """,
            "npm run build",
            "Basic HTTP client with GET/POST/PUT/DELETE, configurable timeout, and retry logic. " +
            "Caching and authentication are planned for a future version.")
    };

    /// <summary>
    /// SD-WEAKEN: Source says "MUST reject with HTTP 429 and include Retry-After header".
    /// Output logs a warning but allows the request through.
    /// </summary>
    public static SeededDefect ConstraintWeakened { get; } = new()
    {
        Id = "SD-WEAKEN",
        Description = "Hard rejection constraint weakened to a warning log",
        DriftCategory = "blocking",
        ExpectedOverallMatch = "FAIL",
        ExpectedDriftCodes = ["CONSTRAINT_WEAKENED"],
        SourceIntent = MakeSourceIntent(
            taskBrief: "Build an API rate limiter middleware. When the rate limit is exceeded, " +
                       "the middleware MUST reject the request with HTTP 429 (Too Many Requests) " +
                       "and MUST include a Retry-After header indicating seconds until the limit resets.",
            acceptanceCriteria:
            [
                ("AC1", "Middleware intercepts requests and tracks rate per client", true),
                ("AC2", "Requests exceeding the limit receive HTTP 429 response", true),
                ("AC3", "429 responses include Retry-After header with seconds until reset", true)
            ],
            explicitConstraints:
            [
                ("EC1", "MUST reject with HTTP 429 when limit exceeded — not warn, not throttle, reject"),
                ("EC2", "MUST include Retry-After header in 429 responses")
            ]),
        DriftedOutput = MakeImplementation(
            """
            // rate-limiter-middleware.ts
            import { Request, Response, NextFunction } from 'express';

            const counters = new Map<string, { count: number; resetAt: number }>();

            export function rateLimiter(limit: number, windowMs: number) {
                return (req: Request, res: Response, next: NextFunction) => {
                    const key = req.ip ?? 'unknown';
                    const now = Date.now();
                    let entry = counters.get(key);

                    if (!entry || now > entry.resetAt) {
                        entry = { count: 0, resetAt: now + windowMs };
                        counters.set(key, entry);
                    }

                    entry.count++;

                    if (entry.count > limit) {
                        // Log the rate limit violation but allow the request through
                        // to avoid disrupting user experience
                        console.warn(`Rate limit exceeded for ${key}: ${entry.count}/${limit}`);
                        res.setHeader('X-RateLimit-Exceeded', 'true');
                    }

                    next(); // Always proceed — rejection would be too disruptive
                };
            }
            """,
            "npm run build",
            "Rate limiter middleware that tracks per-client request rates. " +
            "Logs a warning when limits are exceeded but allows requests through " +
            "to avoid disrupting user experience. Sets X-RateLimit-Exceeded header.")
    };

    /// <summary>
    /// SD-CLEAN: Faithful implementation with no drift.
    /// </summary>
    public static SeededDefect CleanPass { get; } = new()
    {
        Id = "SD-CLEAN",
        Description = "Faithful implementation with no drift — expected PASS",
        DriftCategory = "clean",
        ExpectedOverallMatch = "PASS",
        ExpectedDriftCodes = [],
        SourceIntent = MakeSourceIntent(
            taskBrief: "Build a stack data structure in TypeScript with push, pop, peek, isEmpty, " +
                       "and size operations. The stack should be generic (Stack<T>). " +
                       "Throw an error on pop/peek when empty.",
            acceptanceCriteria:
            [
                ("AC1", "push adds an element to the top of the stack", true),
                ("AC2", "pop removes and returns the top element", true),
                ("AC3", "peek returns the top element without removing it", true),
                ("AC4", "isEmpty returns true when stack has no elements", true),
                ("AC5", "size returns the number of elements", true),
                ("AC6", "pop and peek throw an error when the stack is empty", true)
            ],
            explicitConstraints:
            [
                ("EC1", "Must be generic (Stack<T>)"),
                ("EC2", "Throw an error (not return undefined) on pop/peek when empty")
            ]),
        DriftedOutput = MakeImplementation(
            """
            // stack.ts — Generic stack implementation
            export class Stack<T> {
                private items: T[] = [];

                push(item: T): void {
                    this.items.push(item);
                }

                pop(): T {
                    if (this.isEmpty()) {
                        throw new Error('Cannot pop from an empty stack');
                    }
                    return this.items.pop()!;
                }

                peek(): T {
                    if (this.isEmpty()) {
                        throw new Error('Cannot peek an empty stack');
                    }
                    return this.items[this.items.length - 1];
                }

                isEmpty(): boolean {
                    return this.items.length === 0;
                }

                size(): number {
                    return this.items.length;
                }
            }
            """,
            "npx tsc --outDir dist",
            "Generic Stack<T> with push, pop, peek, isEmpty, size. " +
            "Throws on pop/peek when empty as specified.")
    };

    /// <summary>
    /// SD-MULTI: Multiple drift codes — OMITTED_CONSTRAINT + SCOPE_BROADENED.
    /// Diagnostic only — not threshold-bearing.
    /// </summary>
    public static SeededDefect MultiDrift { get; } = new()
    {
        Id = "SD-MULTI",
        Description = "Multiple drift: constraint dropped AND scope expanded (diagnostic)",
        DriftCategory = "diagnostic",
        ExpectedOverallMatch = "FAIL",
        ExpectedDriftCodes = ["OMITTED_CONSTRAINT", "SCOPE_BROADENED"],
        SourceIntent = MakeSourceIntent(
            taskBrief: "Build a JSON schema validator. It must validate objects against a JSON Schema " +
                       "draft-07 definition. The validator MUST return all validation errors, not just " +
                       "the first one (collect-all-errors mode).",
            acceptanceCriteria:
            [
                ("AC1", "Validates JSON objects against a JSON Schema draft-07 definition", true),
                ("AC2", "Returns all validation errors, not just the first", true),
                ("AC3", "Reports error path (JSON pointer) for each violation", true)
            ],
            explicitConstraints:
            [
                ("EC1", "MUST return ALL errors — collect-all-errors mode, not fail-fast"),
                ("EC2", "JSON Schema draft-07 compliance")
            ]),
        DriftedOutput = MakeImplementation(
            """
            // schema-validator.ts — Validator with fail-fast AND extra features
            import Ajv from 'ajv';

            // Uses fail-fast mode (violates EC1: collect-all-errors)
            const ajv = new Ajv({ allErrors: false });

            export function validate(schema: object, data: unknown): ValidationResult {
                const valid = ajv.validate(schema, data);
                return {
                    valid: valid as boolean,
                    errors: ajv.errors ? [ajv.errors[0]] : [], // Only first error
                    path: ajv.errors?.[0]?.instancePath ?? null
                };
            }

            // Schema generation from TypeScript types (NOT requested — scope broadened)
            export function generateSchema(tsType: string): object {
                // Derives JSON Schema from TypeScript type definitions
                return { type: 'object', properties: {} }; // simplified
            }

            // Schema migration between draft versions (NOT requested)
            export function migrateSchemaDraft(schema: object, fromDraft: string, toDraft: string): object {
                return schema; // placeholder migration
            }

            interface ValidationResult {
                valid: boolean;
                errors: any[];
                path: string | null;
            }
            """,
            "npm run build",
            "JSON Schema validator using Ajv. Returns first error found. " +
            "Also includes schema generation from TypeScript types and draft migration utilities.")
    };

    /// <summary>All 7 seeded defect cases.</summary>
    public static IReadOnlyList<SeededDefect> All { get; } =
    [
        OmittedConstraint,
        InventedRequirement,
        ScopeBroadened,
        ScopeNarrowed,
        ConstraintWeakened,
        CleanPass,
        MultiDrift
    ];

    // ── Helpers ──

    private static ArtifactEnvelope MakeSourceIntent(
        string taskBrief,
        IReadOnlyList<(string id, string criterion, bool verifiable)> acceptanceCriteria,
        IReadOnlyList<(string id, string constraint)> explicitConstraints)
    {
        var acArray = acceptanceCriteria.Select(ac => new Dictionary<string, object>
        {
            ["id"] = ac.id,
            ["criterion"] = ac.criterion,
            ["verifiable"] = ac.verifiable
        }).ToArray();

        var ecArray = explicitConstraints.Select(ec => new Dictionary<string, object>
        {
            ["id"] = ec.id,
            ["constraint"] = ec.constraint
        }).ToArray();

        var payload = new Dictionary<string, object?>
        {
            ["task_brief"] = taskBrief,
            ["acceptance_criteria"] = acArray,
            ["explicit_constraints"] = ecArray,
            ["examples"] = Array.Empty<object>(),
            ["counter_examples"] = Array.Empty<object>(),
            ["preferred_approach"] = null
        };

        return new ArtifactEnvelope
        {
            ArtifactType = "source_intent",
            SchemaVersion = "1",
            ProducedByPhase = "source_intent",
            Payload = JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement
        };
    }

    private static ArtifactEnvelope MakeImplementation(
        string code,
        string buildCommand,
        string notes)
    {
        var payload = new Dictionary<string, object?>
        {
            ["files"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["path"] = "src/index.ts",
                    ["language"] = "typescript",
                    ["content"] = code,
                    ["implements_component_ids"] = new[] { "COMP-1" }
                }
            },
            ["build_command"] = buildCommand,
            ["test_command"] = null,
            ["notes"] = notes
        };

        return new ArtifactEnvelope
        {
            ArtifactType = "implementation",
            SchemaVersion = "1",
            ProducedByPhase = "implementation",
            Payload = JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement
        };
    }
}
