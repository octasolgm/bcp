import {
  formatPointPageRef,
  parsePdfPageFromReference,
  resolveRegulationPdfPage,
  storedPageToViewerPage,
} from './regulation-pdf-page';

describe('regulation-pdf-page', () => {
  it('parses stored page labels', () => {
    expect(parsePdfPageFromReference('6.2 · p. 14')).toBe(14);
    expect(parsePdfPageFromReference('p. 7')).toBe(7);
    expect(parsePdfPageFromReference('Page 9')).toBe(9);
  });

  it('shifts stored pageReference one viewer page earlier', () => {
    expect(storedPageToViewerPage(14)).toBe(13);
    expect(resolveRegulationPdfPage('6.2 · p. 14', null)).toBe(13);
    expect(formatPointPageRef('6.2 · p. 14', null)).toBe('p. 13');
  });

  it('uses official CBUAE viewer pages instead of the stored -1 shift', () => {
    expect(formatPointPageRef('3.2 · p. 15', null, { docName: 'CBUAE_EN_3945_VER2', pointNumber: '3.2' })).toBe(
      'p. 15',
    );
    expect(resolveRegulationPdfPage('3.1 · p. 15', null, { docName: 'CBUAE_EN_3945_VER2', pointNumber: '3.1' })).toBe(
      14,
    );
    expect(resolveRegulationPdfPage('3.3 · p. 16', null, { docName: 'CBUAE_EN_3945_VER2', pointNumber: '3.3' })).toBe(
      15,
    );
  });

  it('trusts snapshot pdfPage without shifting', () => {
    expect(resolveRegulationPdfPage('6.2 · p. 14', 14)).toBe(14);
    expect(formatPointPageRef('6.2 · p. 14', 14)).toBe('p. 14');
  });
});
