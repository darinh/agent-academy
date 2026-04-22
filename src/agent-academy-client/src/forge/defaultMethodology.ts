/**
 * Default methodology JSON for the "New Run" form.
 * Based on the spike-default-v1 methodology from docs/forge-spike/methodology.json.
 */
export const DEFAULT_METHODOLOGY_JSON = JSON.stringify(
  {
    id: "spike-default-v1",
    description: "Five-phase software engineering pipeline",
    max_attempts_default: 3,
    model_defaults: {
      generation: "gpt-4o",
      judge: "gpt-4o-mini",
    },
    phases: [
      {
        id: "requirements",
        goal: "Decompose the task brief into testable functional and non-functional requirements.",
        inputs: [],
        output_schema: "requirements/v1",
        instructions:
          "Read the task brief carefully. Produce a complete requirements artifact. Make assumptions explicit in open_questions[].assumed_answer rather than refusing.",
      },
      {
        id: "contract",
        goal: "Define the external interface the implementation must satisfy.",
        inputs: ["requirements"],
        output_schema: "contract/v1",
        instructions:
          "Treat the requirements as ground truth. Every must-priority FR must be satisfied by at least one interface. Provide concrete signatures.",
      },
      {
        id: "function_design",
        goal: "Decompose the contract into internal components, responsibilities, and data flow.",
        inputs: ["requirements", "contract"],
        output_schema: "function_design/v1",
        instructions:
          "Produce a DAG of components. Each component must have a single, narrow responsibility. Do not write code — only structural design.",
      },
      {
        id: "implementation",
        goal: "Produce the file contents that realize the function design.",
        inputs: ["contract", "function_design"],
        output_schema: "implementation/v1",
        instructions:
          "Output complete, runnable file contents — no placeholders, no TODOs. Use the contract signatures verbatim.",
      },
      {
        id: "review",
        goal: "Adversarially review the implementation against requirements, contract, and design.",
        inputs: ["requirements", "contract", "function_design", "implementation"],
        output_schema: "review/v1",
        instructions:
          "Adopt an adversarial stance: assume the implementation is wrong until proven otherwise. Surface every defect.",
      },
    ],
  },
  null,
  2,
);
