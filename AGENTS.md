<!-- # AGENTS -->
<!-- # -------------------------------------------------------------------->
<!-- # Define the single automation agent (Codex) and point it at the    -->
<!-- # README so it can discover the detailed requirements before coding.-->
<!-- # -------------------------------------------------------------------->

codex:
  description: >
    Implements repository automation tasks. For this project,
    Codex must build the temporary bridge that moves messages
    from Amazon SQS to Azure Queue Storage.

  # Entry point for Codex’s reasoning loop
  entry_file: README.md

  # High‑level workflow the agent should follow
  actions:
    - step: "Open README.md and locate the section 'SQS → Azure Queue Bridge'."
    - step: "Extract acceptance criteria, configuration keys, and folder layout."
    - step: "Generate the Azure Function code and supporting modules described."
    - step: "Update the solution, build, run tests, and commit."
    - step: "Raise a pull‑request for human review."
