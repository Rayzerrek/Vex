# Nushell emits command boundaries itself; retain the user's other integration settings.
$env.config = ($env.config | upsert shell_integration.osc133 true | upsert shell_integration.osc7 true)
