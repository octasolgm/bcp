import { CBUAE_OFFICIAL_VIEWER_PAGES, officialCbuaeViewerPage } from './cbuae-official-pages';

describe('cbuae-official-pages', () => {
  it('maps 3.2 to Chrome page 15', () => {
    expect(CBUAE_OFFICIAL_VIEWER_PAGES['3.2']).toBe(15);
    expect(officialCbuaeViewerPage('CBUAE_EN_3945_VER2', '3.2')).toBe(15);
  });

  it('keeps 3.1 on Chrome page 14', () => {
    expect(officialCbuaeViewerPage('CBUAE_EN_3945_VER2 (v3)', '3.1')).toBe(14);
  });

  it('does not apply to non-CBUAE docs', () => {
    expect(officialCbuaeViewerPage('TFS Guidelines', '3.2')).toBeNull();
  });
});
