// Generates docs/api/openapi.json (OpenAPI 3.1) from the field metadata. Node only, no network, no dependencies.
// Deterministic: the output is JSON.stringify with sorted keys and a two-space indent, there are no timestamps, line ends are LF.
// JSON has no comments, so the "generated, do not edit" notice is in info.description.
// Usage: node tools/openapi-from-metadata/generate.mjs [metadata.json] [--out docs/api/openapi.json]
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const DEFAULT_METADATA = resolve(here, 'field-metadata.json');
const DEFAULT_OUT = resolve(here, '..', '..', 'docs', 'api', 'openapi.json');

const METHODS = { list: 'get', write: 'put', delete: 'delete' };
const AMOUNT_PATTERN = '^-?[0-9]{1,16}(\\.[0-9]{1,2})?$';

const ref = (kind, name) => ({ $ref: `#/components/${kind}/${name}` });
const json = (schema) => ({ 'application/json': { schema } });
const problem = (description) => ({ description, content: { 'application/problem+json': { schema: ref('schemas', 'ProblemDetails') } } });

// ---- conventions shared by every endpoint (US-133, US-134) ----

const RESPONSES = {
  BadRequest: problem('The request is malformed: an unreadable body, an unknown parameter or a value out of range. errors names what is wrong.'),
  Conflict: problem('The record behind the external id was deleted and is not restored by an upsert.'),
  Forbidden: problem('The signed-in user may not do this, or a write with the session cookie lacks the X-Zephyr-Csrf header.'),
  NotFound: problem('The record does not exist or was deleted.'),
  Unauthorized: problem('Nobody is signed in. Sign in and try again.'),
  ValidationFailed: problem('One or more fields are invalid. errors has one entry per field.'),
};
const STATUS_RESPONSES = { 400: 'BadRequest', 401: 'Unauthorized', 403: 'Forbidden', 404: 'NotFound', 409: 'Conflict', 422: 'ValidationFailed' };
const errors = (...codes) => Object.fromEntries(codes.map((code) => [code, ref('responses', STATUS_RESPONSES[code])]));

const PARAMETERS = {
  Cursor: { name: 'cursor', in: 'query', description: 'The nextCursor of the previous page. Leave it out for the first page.', schema: { type: 'string' } },
  Csrf: { name: 'X-Zephyr-Csrf', in: 'header', description: 'Send 1 on writes that use the session cookie (the web app does). Without it the write is refused with 403.', schema: { type: 'string', enum: ['1'] } },
  Filter: {
    name: 'filter',
    in: 'query',
    description: 'Filters as filter[field][op]=value, where op is eq, ne, gt, gte, lt, lte, contains (text) or in (comma-separated values). Every filter must match.',
    style: 'deepObject',
    explode: true,
    schema: { type: 'object', additionalProperties: { type: 'object', additionalProperties: { type: 'string' } } },
  },
  IfMatch: { name: 'If-Match', in: 'header', description: 'The ETag you last read. Needed to change an existing record with PATCH: 428 when missing, 412 when the record changed since.', schema: { type: 'string' } },
  Limit: { name: 'limit', in: 'query', description: 'Page size from 1 to 200, default 50. A larger value is a 400, it is not capped.', schema: { type: 'integer', minimum: 1, maximum: 200, default: 50 } },
  Sort: { name: 'sort', in: 'query', description: 'Comma-separated field names with a leading minus for descending, for example -updatedAt. id is always the last key.', schema: { type: 'string' } },
};

const HEADERS = {
  ETag: { description: 'Strong ETag of the record. Send it back in If-Match to change the record.', schema: { type: 'string' } },
  Location: { description: 'URL of the created record.', schema: { type: 'string' } },
};

const SHARED_SCHEMAS = {
  FieldError: {
    type: 'object',
    description: 'One failing field.',
    required: ['field', 'code', 'message'],
    properties: {
      field: { type: 'string', description: 'The field or parameter that failed, for example name or filter[month][gte].' },
      code: { type: 'string', description: 'Machine-readable error code, for example VALIDATION.' },
      message: { type: 'string', description: 'What is wrong and how to fix it.' },
    },
  },
  Money: {
    type: 'object',
    description: 'An amount in its own currency and in SEK, stamped with the exchange rate of the record month. Amounts are strings.',
    required: ['amount', 'currency', 'amountSEK', 'rateDate'],
    properties: {
      amount: { type: 'string', description: 'Amount with 2 decimals.', pattern: AMOUNT_PATTERN },
      currency: { type: 'string', description: 'Currency code as three capital letters.', pattern: '^[A-Z]{3}$' },
      amountSEK: { type: 'string', description: 'Amount in SEK with 2 decimals.', pattern: AMOUNT_PATTERN },
      rateDate: { type: 'string', format: 'date', description: 'First day of the month whose exchange rate was used.' },
    },
  },
  Page: {
    type: 'object',
    description: 'One page of a list response (keyset paging).',
    required: ['items', 'nextCursor'],
    properties: {
      items: { type: 'array', description: 'The rows of this page.', items: {} },
      nextCursor: { type: ['string', 'null'], description: 'Send as cursor to get the next page. Null on the last page.' },
    },
  },
  ProblemDetails: {
    type: 'object',
    description: 'RFC 9457 problem, served as application/problem+json.',
    required: ['type', 'title', 'status', 'detail', 'traceId'],
    properties: {
      type: { type: 'string', description: 'A URN naming the problem, for example urn:zephyr:problem:validation, or about:blank.' },
      title: { type: 'string', description: 'Short summary of the problem.' },
      status: { type: 'integer', description: 'The HTTP status code.' },
      detail: { type: 'string', description: 'What went wrong and what to do about it.' },
      code: { type: 'string', description: 'Machine-readable error code, for example VALIDATION.' },
      errors: { type: 'array', description: 'One entry per failing field.', items: ref('schemas', 'FieldError') },
      traceId: { type: 'string', description: 'Quote this when you report a problem.' },
    },
  },
};

// ---- routes that are not generated from entity metadata: health and sign-in, served outside /api/v1 ----

const SITE_ROOT = [{ url: '/', description: 'Site root: this route is not under /api/v1.' }];
const health = (values) => ({
  type: 'object',
  required: ['status'],
  properties: { status: { type: 'string', description: 'Health of the API and its database.', enum: values } },
});
const SYSTEM_TAGS = [
  { name: 'Authentication', description: 'Sign-in and session. Zephyr stores no passwords: an identity provider signs users in.' },
  { name: 'System', description: 'Operational endpoints.' },
];
const SYSTEM_PATHS = {
  '/auth/denied': {
    servers: SITE_ROOT,
    get: {
      operationId: 'getSignInDenied',
      tags: ['Authentication'],
      summary: 'Show why sign-in was refused',
      description: 'Anonymous. Where the browser lands after a refused sign-in. Always answers 403.',
      security: [],
      parameters: [{ name: 'reason', in: 'query', description: 'failed when sign-in did not complete. Otherwise the user is not set up in Zephyr.', schema: { type: 'string' } }],
      responses: errors(403),
    },
  },
  '/auth/login': {
    servers: SITE_ROOT,
    get: {
      operationId: 'signIn',
      tags: ['Authentication'],
      summary: 'Start sign-in',
      description: 'Anonymous. Redirects the browser to the identity provider (OpenID Connect code flow with PKCE).',
      security: [],
      parameters: [
        { name: 'provider', in: 'query', description: 'Name of a configured sign-in provider. The first configured provider is used when left out.', schema: { type: 'string' } },
        { name: 'returnUrl', in: 'query', description: 'A path on this site to return to after sign-in, for example /accounts.', schema: { type: 'string', pattern: '^/' } },
      ],
      responses: { 302: { description: 'Redirect to the identity provider.', headers: { Location: ref('headers', 'Location') } }, ...errors(400) },
    },
  },
  '/auth/logout': {
    servers: SITE_ROOT,
    post: {
      operationId: 'signOut',
      tags: ['Authentication'],
      summary: 'Sign out',
      description: 'Ends the session.',
      parameters: [ref('parameters', 'Csrf')],
      responses: { 204: { description: 'Signed out.' }, ...errors(401, 403) },
    },
  },
  '/auth/me': {
    servers: SITE_ROOT,
    get: {
      operationId: 'getCurrentUser',
      tags: ['Authentication'],
      summary: 'Get the signed-in user',
      description: 'The active user behind the session.',
      responses: { 200: { description: 'The signed-in user.', content: json(ref('schemas', 'CurrentUser')) }, ...errors(401, 403) },
    },
  },
  '/health': {
    servers: SITE_ROOT,
    get: {
      operationId: 'getHealth',
      tags: ['System'],
      summary: 'Check health',
      description: 'Anonymous. 200 when the API is up and the database answers, 503 otherwise.',
      security: [],
      responses: {
        200: { description: 'The API and the database are reachable.', content: json(health(['Healthy'])) },
        503: { description: 'The database is not reachable.', content: json(health(['Unhealthy', 'Degraded'])) },
      },
    },
  },
};

// ---- field metadata to schema ----

const TYPES = {
  string: { type: 'string' },
  uuid: { type: 'string', format: 'uuid' },
  'date-time': { type: 'string', format: 'date-time' },
  date: { type: 'string', format: 'date' },
  decimal: { type: 'string', pattern: '^-?[0-9]+(\\.[0-9]+)?$' },
  integer: { type: 'integer' },
  boolean: { type: 'boolean' },
};

/** One metadata field to a JSON Schema: type, nullability, enum, pattern and max length all come from the field. */
function fieldSchema(field, where) {
  if (field.type === 'money') {
    const money = ref('schemas', 'Money');
    return field.nullable ? { oneOf: [money, { type: 'null' }] } : money;
  }

  const array = field.type === 'string[]';
  const base = array ? TYPES.string : TYPES[field.type];
  if (!base) {
    throw new Error(`${where}: unknown field type '${field.type}'. Use one of: ${[...Object.keys(TYPES), 'string[]', 'money'].join(', ')}.`);
  }

  const nullable = field.nullable && !array;
  const pattern = field.pattern ?? base.pattern;
  const scalar = {
    type: nullable ? [base.type, 'null'] : base.type,
    ...(base.format && { format: base.format }),
    ...(field.description && !array && { description: field.description }),
    ...(field.enum && { enum: nullable ? [...field.enum, null] : field.enum }),
    ...(pattern && { pattern }),
    ...(field.maxLength !== undefined && { maxLength: field.maxLength }),
  };
  return array ? { type: 'array', ...(field.description && { description: field.description }), items: scalar } : scalar;
}

function objectSchema(description, entries, required, where) {
  return {
    type: 'object',
    description,
    ...(required.length > 0 && { required }),
    properties: Object.fromEntries(entries.map(([name, field]) => [name, fieldSchema(field, `${where}.${name}`)])),
  };
}

function pathParameters(op) {
  return Object.entries(op.pathParams ?? {}).map(([name, { description, ...field }]) => ({
    name,
    in: 'path',
    required: true,
    description,
    schema: fieldSchema(field, `${op.operationId}.${name}`),
  }));
}

function operation(op, entity) {
  const head = { operationId: op.operationId, tags: [entity.tag], summary: op.summary };
  const csrf = ref('parameters', 'Csrf');
  if (op.kind === 'list') {
    const items = { type: 'array', items: ref('schemas', op.entity) };
    return {
      ...head,
      description: `${op.description} Filter and sort fields: ${op.filterFields.join(', ')}.`,
      parameters: ['Limit', 'Cursor', 'Filter', 'Sort'].map((name) => ref('parameters', name)),
      responses: {
        200: {
          description: 'One page of results. nextCursor is null on the last page.',
          content: json({ allOf: [ref('schemas', 'Page'), { type: 'object', properties: { items } }] }),
        },
        ...errors(400, 401, 403),
      },
    };
  }

  if (op.kind === 'delete') {
    return {
      ...head,
      description: op.description,
      parameters: [...pathParameters(op), csrf],
      responses: { 204: { description: 'Deleted. The record no longer appears in reads.' }, ...errors(401, 403, 404) },
    };
  }

  const body = json(ref('schemas', op.entity));
  const etag = { ETag: ref('headers', 'ETag') };
  return {
    ...head,
    description: op.description,
    parameters: [...pathParameters(op), csrf],
    requestBody: { required: true, content: json(ref('schemas', `${op.entity}Input`)) },
    responses: {
      200: { description: 'Updated.', headers: etag, content: body },
      201: { description: 'Created. Location points at the new record.', headers: { ...etag, Location: ref('headers', 'Location') }, content: body },
      ...errors(400, 401, 403, ...(op.extraResponses ?? []), 422),
    },
  };
}

/** The whole OpenAPI document as a plain object. Throws an Error with a plain message when the metadata is inconsistent. */
export function buildSpec(metadata) {
  const entities = metadata.entities ?? {};
  const schemas = { ...SHARED_SCHEMAS };
  const tags = [...SYSTEM_TAGS];
  for (const [name, entity] of Object.entries(entities)) {
    const fields = Object.entries(entity.fields ?? {});
    schemas[name] = objectSchema(entity.description, fields, fields.map(([field]) => field), name);
    const writable = fields.filter(([, field]) => field.writable);
    if (writable.length > 0) {
      const required = writable.filter(([, field]) => field.required).map(([field]) => field);
      schemas[`${name}Input`] = objectSchema(`The fields a client may send for ${name}.`, writable, required, `${name}Input`);
    }

    if (entity.tag) {
      tags.push({ name: entity.tag, description: entity.description });
    }
  }

  const paths = {};
  for (const op of metadata.operations ?? []) {
    const method = METHODS[op.kind];
    if (!method) {
      throw new Error(`${op.operationId}: unknown operation kind '${op.kind}'. Use one of: ${Object.keys(METHODS).join(', ')}.`);
    }

    if (!entities[op.entity]?.tag) {
      throw new Error(`${op.operationId}: entity '${op.entity}' is not in the metadata or has no tag.`);
    }

    if (op.kind === 'write' && !schemas[`${op.entity}Input`]) {
      throw new Error(`${op.operationId}: entity '${op.entity}' has no writable field, so it cannot have a write operation.`);
    }

    const item = (paths[op.path] ??= {});
    if (item[method]) {
      throw new Error(`${op.operationId}: ${method.toUpperCase()} ${op.path} is defined twice.`);
    }

    item[method] = operation(op, entities[op.entity]);
  }

  return {
    openapi: '3.1.0',
    info: {
      title: 'Zephyr API',
      version: '1.0.0',
      description: 'Headless REST API of Zephyr (CRM and project operations). This file is generated by tools/openapi-from-metadata/generate.mjs from field metadata: generated, do not edit. To change it, change the metadata and run the generator. Conventions: base /api/v1, camelCase JSON, keyset paging with limit and cursor, filter[field][op] and sort, strong ETag with If-Match, errors as application/problem+json.',
      contact: { name: 'Zephyr team' },
    },
    servers: [{ url: '/api/v1', description: 'Versioned API base. Routes with a site-root server of their own are served outside it.' }],
    security: [{ cookieAuth: [] }],
    tags: tags.sort((a, b) => (a.name < b.name ? -1 : 1)),
    paths: { ...paths, ...SYSTEM_PATHS },
    components: {
      headers: HEADERS,
      parameters: PARAMETERS,
      responses: RESPONSES,
      schemas,
      securitySchemes: {
        cookieAuth: {
          type: 'apiKey',
          in: 'cookie',
          name: 'zephyr.session',
          description: 'Session cookie set after sign-in with an identity provider. Writes that use it also need the X-Zephyr-Csrf header.',
        },
      },
    },
  };
}

/** Object keys sorted (by UTF-16 code unit, so the result never depends on the locale) at every depth; arrays keep their order. */
function sortKeys(value) {
  if (Array.isArray(value)) {
    return value.map(sortKeys);
  }

  if (value === null || typeof value !== 'object') {
    return value;
  }

  return Object.fromEntries(
    Object.entries(value)
      .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))
      .map(([key, item]) => [key, sortKeys(item)]),
  );
}

/** The file content: `openapi` and `info` come first, every other key is sorted, two-space indent, one trailing LF. */
export function toJson(spec) {
  const { openapi, info, ...rest } = spec;
  return `${JSON.stringify({ openapi, info: sortKeys(info), ...sortKeys(rest) }, null, 2)}\n`;
}

function main(argv) {
  let metadataPath = DEFAULT_METADATA;
  let out = DEFAULT_OUT;
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--out') {
      if (!argv[i + 1]) {
        throw new Error('--out needs a file path.');
      }

      out = resolve(argv[++i]);
    } else {
      metadataPath = resolve(argv[i]);
    }
  }

  const metadata = JSON.parse(readFileSync(metadataPath, 'utf8'));
  mkdirSync(dirname(out), { recursive: true });
  writeFileSync(out, toJson(buildSpec(metadata)));
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    main(process.argv.slice(2));
  } catch (error) {
    console.error(`generate.mjs: ${error.message}`);
    process.exit(1);
  }
}
