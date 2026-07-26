import http from 'node:http';

const proposal = {
  summary: 'Deterministic E2E proposal from renovation feedback.',
  rule: {
    combinator: 'all',
    conditions: [
      { field: 'condition', operator: 'contains', value: 'renovation' }
    ]
  }
};

http.createServer((request, response) => {
  if (request.method !== 'POST' || request.url !== '/api/generate') {
    response.writeHead(404).end();
    return;
  }

  request.resume();
  request.on('end', () => {
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end(JSON.stringify({ response: JSON.stringify(proposal) }));
  });
}).listen(11434, '0.0.0.0');
