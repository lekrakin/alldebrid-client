export class Setting {
  key: string;
  parentKey: null | string;
  value: boolean | number | null | string;
  displayName: null | string;
  description: null | string;
  type: string;
  settings: Setting[];
  enumValues: null | { [key: string]: string };
  minimum: null | number;
  maximum: null | number;
  isSecret: boolean;
}
