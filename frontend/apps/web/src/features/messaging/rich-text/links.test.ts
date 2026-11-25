import { describe, expect, it } from 'vitest';
import { distinctWebLinks, type Block } from './blocks';

const link = (href: string) => ({
  type: 'text' as const,
  text: 'x',
  marks: [{ type: 'link' as const, href }],
});

describe('distinctWebLinks', () => {
  it('DistinctWebLinks_WhenSameAddressTwiceAndMailto_CountsWebAddressesOnce', () => {
    const blocks: Block[] = [
      { type: 'paragraph', children: [link('https://a.test/'), link('https://a.test/')] },
      {
        type: 'bulleted_list',
        children: [
          { type: 'list_item', children: [link('http://b.test/')] },
          { type: 'list_item', children: [link('mailto:x@a.test')] },
        ],
      },
    ];

    expect(distinctWebLinks(blocks)).toBe(2);
  });
});
