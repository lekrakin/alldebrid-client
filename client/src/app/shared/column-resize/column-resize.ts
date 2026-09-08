export function clampColumnWidth(width: number, min: number, max: number): number {
  return Math.round(Math.max(min, Math.min(max, Number.isNaN(width) ? min : width)));
}

export function columnWidthForKey(
  key: string,
  width: number,
  min: number,
  max: number,
  largeStep: boolean
): number | null | undefined {
  if (key === 'Enter') {
    return null;
  }
  if (key === 'Home') {
    return min;
  }
  if (key === 'End') {
    return max;
  }
  if (key !== 'ArrowLeft' && key !== 'ArrowRight') {
    return undefined;
  }
  const step = largeStep ? 64 : 16;
  return clampColumnWidth(width + (key === 'ArrowLeft' ? -step : step), min, max);
}
