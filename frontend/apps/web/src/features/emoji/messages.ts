import type { Messages } from '@backroom/i18n';
import type { EmojiGroupKey } from './data';

// The picker is not drawn in docs/design; the group names follow Unicode's (CLDR) and the Turkish UI says "sen".
type Key =
  | 'picker'
  | 'search'
  | 'searchResults'
  | 'resultCount'
  | 'noResults'
  | 'categories'
  | 'grid'
  | 'recent'
  | EmojiGroupKey
  | 'skinTone'
  | 'tone0'
  | 'tone1'
  | 'tone2'
  | 'tone3'
  | 'tone4'
  | 'tone5';

export const messages: Messages<Key> = {
  en: {
    picker: 'Emoji picker',
    search: 'Search emoji',
    searchResults: 'Search results',
    resultCount: '{count} emoji found',
    noResults: 'No emoji found.',
    categories: 'Emoji categories',
    grid: 'Emoji',
    recent: 'Recently used',
    'smileys-emotion': 'Smileys and emotion',
    'people-body': 'People and body',
    'animals-nature': 'Animals and nature',
    'food-drink': 'Food and drink',
    'travel-places': 'Travel and places',
    activities: 'Activities',
    objects: 'Objects',
    symbols: 'Symbols',
    flags: 'Flags',
    skinTone: 'Skin tone',
    tone0: 'Default',
    tone1: 'Light',
    tone2: 'Medium-light',
    tone3: 'Medium',
    tone4: 'Medium-dark',
    tone5: 'Dark',
  },
  tr: {
    picker: 'Emoji seçici',
    search: 'Emoji ara',
    searchResults: 'Arama sonuçları',
    resultCount: '{count} emoji bulundu',
    noResults: 'Emoji bulunamadı.',
    categories: 'Emoji kategorileri',
    grid: 'Emojiler',
    recent: 'Son kullanılanlar',
    'smileys-emotion': 'İfadeler ve duygular',
    'people-body': 'İnsanlar ve vücut',
    'animals-nature': 'Hayvanlar ve doğa',
    'food-drink': 'Yiyecek ve içecek',
    'travel-places': 'Seyahat ve yerler',
    activities: 'Etkinlikler',
    objects: 'Nesneler',
    symbols: 'Semboller',
    flags: 'Bayraklar',
    skinTone: 'Ten rengi',
    tone0: 'Varsayılan',
    tone1: 'Açık',
    tone2: 'Orta açık',
    tone3: 'Orta',
    tone4: 'Orta koyu',
    tone5: 'Koyu',
  },
};
