-- One small synchronous commit: a WAL record of a few bytes that no table sees (docs: load-tests/README.md, "WAL flush probe").
SELECT pg_logical_emit_message(true, 'backroom.flush-probe', '');
