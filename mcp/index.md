# MCP Index

Short entry point for server-side prompts, policies, resources, and validators.

| Item | Purpose |
|---|---|
| [README.md](README.md) | MCP contracts landing page |
| [prompts/](prompts) | Reusable prompt templates for safe operations |
| [policies/](policies) | Rules for mutation, serialization, and verification |
| [resources/](resources) | Scenario recipes, build smoke steps, and workflow rules |
| [validators/](validators) | Tool/workflow coverage matrix |

## Main files

### Prompts

- `prompts/safe_scene_edit.md`
- `prompts/safe_prefab_edit.md`
- `prompts/safe_graph_edit.md`
- `prompts/implement_gameplay_feature.md`
- `prompts/release_preflight.md`

### Policies

- `policies/mutation_policy.md`
- `policies/serialization_policy.md`
- `policies/verification_policy.md`

### Resources

- `resources/build_smoke.md`
- `resources/scenario_macros.md`
- `resources/game_design_rules.md`
- `resources/graph_semantic_patterns.md`
- `resources/localization_rules.md`
- `resources/quality_profile_rules.md`
- `resources/release_matrix.md`
- `resources/save_policy_rules.md`

### Validators

- `validators/validator_matrix.md`

## How to use

1. Start with the prompt that matches the task.
2. Check policies if the task mutates content or state.
3. Use resources for reusable workflow recipes.
4. Validate coverage with the validator matrix when the contract changes.
