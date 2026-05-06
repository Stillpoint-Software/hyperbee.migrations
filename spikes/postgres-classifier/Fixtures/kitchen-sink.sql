-- Kitchen-sink Postgres schema for classifier spike.
--
-- Goal: exercise every high-risk parsing surface the production classifier will face
-- when ingesting `pg_dump --schema-only` output. This is INPUT to the classifier
-- (we apply this schema to a real Postgres container, then `pg_dump` it back, then
-- classify the dump).
--
-- High-risk surfaces deliberately included:
--   - Dollar-quoted function bodies (plain $$...$$ AND tagged $tag$...$tag$)
--   - Function bodies containing semicolons, single quotes, comment markers
--   - Nested dollar quotes (functions that emit functions)
--   - RLS policies with USING + WITH CHECK clauses spanning multiple lines
--   - Declarative range/list partitioning
--   - Custom types (composite, domain, enum)
--   - Identity columns (GENERATED ALWAYS AS IDENTITY)
--   - Generated stored columns (GENERATED ALWAYS AS (expr) STORED)
--   - Triggers + trigger functions
--   - Sequences with non-default cache/start
--   - Extensions
--   - Comments INSIDE statements (-- and /* */)
--   - String literals containing semicolons and quote-escapes ('it''s')
--   - Schema-qualified everything

CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE SCHEMA app;

-- ============================================================================
-- Custom types
-- ============================================================================

CREATE TYPE app.order_status AS ENUM ('pending', 'paid', 'shipped', 'cancelled');

CREATE DOMAIN app.email AS text
    CHECK (VALUE ~* '^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$');

CREATE TYPE app.address AS (
    line1 text,
    line2 text,
    city  text,
    zip   text
);

-- ============================================================================
-- Sequences (custom cache + start)
-- ============================================================================

CREATE SEQUENCE app.order_seq START WITH 1000 INCREMENT BY 1 CACHE 50;

-- ============================================================================
-- Tables with identity, generated, custom-type columns
-- ============================================================================

CREATE TABLE app.customer (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email       app.email NOT NULL UNIQUE,
    full_name   text NOT NULL,
    -- a tricky default: contains a semicolon and quote-escape inside the string literal
    note        text DEFAULT 'created; with ''quotes'' and -- pseudo comment',
    address     app.address,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE app."order" (
    id          bigint NOT NULL DEFAULT nextval('app.order_seq'),
    customer_id bigint NOT NULL REFERENCES app.customer(id),
    status      app.order_status NOT NULL DEFAULT 'pending',
    subtotal    numeric(12,2) NOT NULL,
    tax         numeric(12,2) NOT NULL DEFAULT 0,
    -- generated column: the (expr) parens contain a comma; classifier must not split there
    total       numeric(12,2) GENERATED ALWAYS AS (subtotal + tax) STORED,
    placed_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (id, placed_at)
) PARTITION BY RANGE (placed_at);

CREATE TABLE app.order_2025 PARTITION OF app."order"
    FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');

CREATE TABLE app.order_2026 PARTITION OF app."order"
    FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');

CREATE TABLE app.audit_log (
    id        bigserial PRIMARY KEY,
    actor     text NOT NULL,
    action    text NOT NULL,
    payload   jsonb NOT NULL,
    logged_at timestamptz NOT NULL DEFAULT now()
);

-- List-partitioned table
CREATE TABLE app.feature_flag (
    tenant_id text NOT NULL,
    name      text NOT NULL,
    enabled   boolean NOT NULL,
    PRIMARY KEY (tenant_id, name)
) PARTITION BY LIST (tenant_id);

CREATE TABLE app.feature_flag_acme PARTITION OF app.feature_flag FOR VALUES IN ('acme');
CREATE TABLE app.feature_flag_default PARTITION OF app.feature_flag DEFAULT;

-- ============================================================================
-- Indexes
-- ============================================================================

CREATE INDEX customer_email_lower_idx ON app.customer (lower(email));
CREATE INDEX order_customer_idx ON app."order" (customer_id);
CREATE UNIQUE INDEX customer_full_name_uniq ON app.customer (full_name) WHERE full_name <> '';

-- ============================================================================
-- Functions: plain $$ body
-- ============================================================================

CREATE FUNCTION app.normalize_email(input text) RETURNS text
LANGUAGE sql IMMUTABLE AS $$
    SELECT lower(trim(input));
$$;

-- Function with body containing semicolons, comments, quotes
-- Note: this function uses a tagged $body$ outer quote because we want the BODY to
-- contain literal '$' || '$' sequences. With a plain $$ outer quote, even a
-- semicolon-laden line comment inside the body that mentions a doubled-dollar
-- sequence would close the outer quote (Postgres' lexer doesn't recognize
-- plpgsql's `--` comments inside the dollar-quoted body).
CREATE FUNCTION app.is_premium(c_id bigint) RETURNS boolean
LANGUAGE plpgsql STABLE AS $body$
DECLARE
    cnt int;
BEGIN
    -- count orders; semicolons; here are fine, and '$' || '$' is opaque to body
    SELECT count(*) INTO cnt FROM app."order" WHERE customer_id = c_id;
    /* multi-line
       comment with ; and 'quotes' */
    RETURN cnt > 10;
END;
$body$;

-- ============================================================================
-- Functions: tagged dollar-quote $body$ (used because body contains $$)
-- ============================================================================

-- The body contains a nested $$...$$ literal, so the outer dollar-quote uses a
-- distinct tag ($dq$) that does NOT appear anywhere inside the body. Note that
-- inside a dollar-quoted string, "--" line comments are NOT recognized; the
-- matching close tag is the ONLY thing that ends the literal. So the outer
-- tag must be carefully chosen to not appear (even inside strings/comments)
-- in the body.
CREATE FUNCTION app.dynamic_query(tbl text) RETURNS SETOF record
LANGUAGE plpgsql AS $dq$
BEGIN
    RETURN QUERY EXECUTE format($$SELECT * FROM %I WHERE 1=1$$, tbl);
END;
$dq$;

-- ============================================================================
-- Trigger function + trigger
-- ============================================================================

CREATE FUNCTION app.audit_trg() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO app.audit_log(actor, action, payload)
    VALUES (current_user, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END;
$$;

CREATE TRIGGER customer_audit
AFTER INSERT OR UPDATE ON app.customer
FOR EACH ROW EXECUTE FUNCTION app.audit_trg();

-- ============================================================================
-- Row-Level Security
-- ============================================================================

ALTER TABLE app.customer ENABLE ROW LEVEL SECURITY;

CREATE POLICY customer_tenant_isolation ON app.customer
    AS PERMISSIVE
    FOR ALL
    TO PUBLIC
    USING (
        -- multi-line USING with parens
        current_setting('app.tenant_id', true) IS NOT NULL
        AND email LIKE current_setting('app.tenant_id', true) || '%'
    )
    WITH CHECK (
        full_name <> ''
    );

-- ============================================================================
-- Views
-- ============================================================================

CREATE VIEW app.customer_summary AS
    SELECT c.id, c.email, count(o.id) AS order_count
    FROM app.customer c
    LEFT JOIN app."order" o ON o.customer_id = c.id
    GROUP BY c.id, c.email;

CREATE MATERIALIZED VIEW app.customer_summary_mv AS
    SELECT * FROM app.customer_summary
WITH NO DATA;

CREATE INDEX customer_summary_mv_id_idx ON app.customer_summary_mv (id);

-- ============================================================================
-- Comments on objects (these become COMMENT ON statements in dump output)
-- ============================================================================

COMMENT ON TABLE app.customer IS 'Customer; with semicolon and ''quote'' in comment';
COMMENT ON COLUMN app."order".total IS 'Generated: subtotal + tax';
