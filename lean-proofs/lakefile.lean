import Lake
open Lake DSL

package «state-manager-sketch» where
  -- no external dependencies

@[default_target]
lean_lib «StateManager» where
  roots := #[`StateManager]
