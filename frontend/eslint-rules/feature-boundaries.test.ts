import path from 'node:path';
import { RuleTester } from 'eslint';
import { afterAll, describe, it } from 'vitest';
import plugin from './feature-boundaries.js';

// Run RuleTester cases as Vitest tests. ESLint's typings do not declare afterAll, hence Object.assign.
Object.assign(RuleTester, { afterAll, describe, it });

const rule = plugin.rules['feature-boundaries'];
const tester = new RuleTester({ languageOptions: { ecmaVersion: 'latest', sourceType: 'module' } });

const src = path.resolve('/repo/frontend/apps/web/src');
const file = (relative: string) => path.join(src, relative);

tester.run('feature-boundaries', rule, {
  valid: [
    { filename: file('app/app.tsx'), code: "import { X } from '@/features/messaging';" },
    { filename: file('app/app.tsx'), code: "import { X } from '@/features/messaging/index';" },
    { filename: file('app/app.tsx'), code: "import { X } from '../features/messaging';" },
    { filename: file('features/messaging/components/list.tsx'), code: "import { y } from '../hooks/use-y';" },
    {
      filename: file('features/messaging/components/list.tsx'),
      code: "import { y } from '@/features/messaging/hooks/use-y';",
    },
    { filename: file('features/messaging/components/list.tsx'), code: "import { f } from '../../files';" },
    { filename: file('app/app.tsx'), code: "import { useState } from 'react';" },
    { filename: file('app/app.tsx'), code: "import { Button } from '@/components/button';" },
  ],
  invalid: [
    {
      filename: file('app/app.tsx'),
      code: "import { X } from '@/features/messaging/components/list';",
      errors: [{ messageId: 'internal' }],
    },
    {
      filename: file('app/app.tsx'),
      code: "import { X } from '../features/messaging/components/list';",
      errors: [{ messageId: 'internal' }],
    },
    {
      // Sibling feature through a relative path that never mentions "features/".
      filename: file('features/messaging/components/list.tsx'),
      code: "import { f } from '../../files/components/file-row';",
      errors: [{ messageId: 'internal' }],
    },
    {
      filename: file('features/messaging/index.ts'),
      code: "export { f } from '../files/api/get-files';",
      errors: [{ messageId: 'internal' }],
    },
    {
      filename: file('features/messaging/components/list.tsx'),
      code: "const m = import('@/features/files/components/file-row');",
      errors: [{ messageId: 'internal' }],
    },
  ],
});
