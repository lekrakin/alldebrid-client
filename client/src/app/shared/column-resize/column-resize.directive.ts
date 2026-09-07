import { Directive, ElementRef, inject, input, output, signal } from '@angular/core';
import { clampColumnWidth, columnWidthForKey } from './column-resize';

@Directive({
  selector: '[appColumnResize]',
  standalone: true,
  host: {
    role: 'separator',
    tabindex: '0',
    'aria-orientation': 'vertical',
    '[attr.aria-label]': '"Resize " + columnLabel() + " column"',
    '[attr.aria-valuenow]': 'columnWidth()',
    '[attr.aria-valuemin]': 'minWidth()',
    '[attr.aria-valuemax]': 'maxWidth()',
    '[attr.aria-valuetext]': 'columnWidth() + " pixels"',
    title: 'Drag or use arrow keys to resize. Hold Shift for larger steps. Double-click or press Enter to reset.',
    '[class.is-resizing]': 'resizing()',
    '(pointerdown)': 'startResize($event)',
    '(pointermove)': 'moveResize($event)',
    '(pointerup)': 'finishResize($event)',
    '(pointercancel)': 'finishResize($event)',
    '(lostpointercapture)': 'clearPointer()',
    '(keydown)': 'resizeWithKeyboard($event)',
    '(dblclick)': 'resetWidth($event)',
  },
})
export class ColumnResizeDirective {
  public readonly columnLabel = input.required<string>();
  public readonly columnWidth = input.required<number>();
  public readonly minWidth = input.required<number>();
  public readonly maxWidth = input.required<number>();
  public readonly columnWidthChange = output<number | null>();
  protected readonly resizing = signal(false);

  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private pointer: { id: number; startX: number; width: number } | null = null;

  protected startResize(event: PointerEvent): void {
    if (this.pointer || event.button !== 0 || !event.isPrimary) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    this.element.nativeElement.focus({ preventScroll: true });
    this.element.nativeElement.setPointerCapture(event.pointerId);
    this.pointer = { id: event.pointerId, startX: event.clientX, width: this.columnWidth() };
    this.resizing.set(true);
  }

  protected moveResize(event: PointerEvent): void {
    if (this.pointer?.id !== event.pointerId) {
      return;
    }
    this.columnWidthChange.emit(
      clampColumnWidth(this.pointer.width + event.clientX - this.pointer.startX, this.minWidth(), this.maxWidth())
    );
  }

  protected finishResize(event: PointerEvent): void {
    if (this.pointer?.id !== event.pointerId) {
      return;
    }
    if (event.type === 'pointerup') {
      this.moveResize(event);
    }
    if (this.element.nativeElement.hasPointerCapture(event.pointerId)) {
      this.element.nativeElement.releasePointerCapture(event.pointerId);
    }
    this.clearPointer();
  }

  protected clearPointer(): void {
    this.pointer = null;
    this.resizing.set(false);
  }

  protected resizeWithKeyboard(event: KeyboardEvent): void {
    if (event.altKey || event.ctrlKey || event.metaKey) {
      return;
    }
    const width = columnWidthForKey(event.key, this.columnWidth(), this.minWidth(), this.maxWidth(), event.shiftKey);
    if (width === undefined) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    this.columnWidthChange.emit(width);
  }

  protected resetWidth(event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.columnWidthChange.emit(null);
  }
}
