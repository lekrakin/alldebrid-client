import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { parseTemplate } from '@angular/compiler';

test('magnet input uses ngModel without intercepting native paste or text input', () => {
  const path = new URL('../src/app/add-new-torrent/add-new-torrent.component.html', import.meta.url);
  const template = parseTemplate(readFileSync(path, 'utf8'), path.href);
  assert.equal(template.errors, null);

  function findTextarea(nodes) {
    for (const node of nodes) {
      if (node.name === 'textarea') return node;
      const child = findTextarea(node.children ?? []);
      if (child) return child;
    }
  }

  const textarea = findTextarea(template.nodes);
  assert.ok(textarea);
  assert.equal(textarea.inputs.find((input) => input.name === 'ngModel').value.source, 'magnetLink()');
  assert.deepEqual(
    textarea.outputs.map((output) => output.name),
    ['ngModelChange']
  );
  assert.equal(textarea.outputs[0].handler.source, 'magnetLink.set($event)');
});
