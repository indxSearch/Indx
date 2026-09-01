const fs = require('fs');
const path = require('path');

const inputFolder = path.join(__dirname, 'wwwroot/icons');
const outputFolder = path.join(__dirname, 'Indx.Systm.Blazor', 'Icons');

if (!fs.existsSync(outputFolder)) {
  fs.mkdirSync(outputFolder, { recursive: true });
}

// C# built-in type names that would cause ambiguity errors
const RESERVED_NAMES = new Set(['Array', 'String', 'Object', 'Number', 'Bool', 'List']);

// Names that would shadow a root namespace used by the project (class Icons.Microsoft
// makes `@using Microsoft.AspNetCore...` unresolvable everywhere)
const NAMESPACE_COLLISIONS = { Microsoft: 'MicrosoftLogo', Indx: 'IndxLogo', Google: 'GoogleLogo' };

function toPascalCase(filename) {
  const name = filename
    .replace(/[-\s]+(.)/g, (_, c) => c.toUpperCase()) // hyphen/space → capitalize next
    .replace(/^(.)/, c => c.toUpperCase());            // capitalize first char
  if (NAMESPACE_COLLISIONS[name]) return NAMESPACE_COLLISIONS[name];
  return RESERVED_NAMES.has(name) ? `${name}Type` : name;
}

const files = fs.readdirSync(inputFolder).filter(f => f.endsWith('.svg'));

files.forEach(file => {
  const componentName = toPascalCase(path.basename(file, '.svg'));
  const svgContent = fs.readFileSync(path.join(inputFolder, file), 'utf8');

  const viewBoxMatch = svgContent.match(/viewBox="([^"]+)"/);
  const pathMatches = svgContent.match(/<path[^>]*\/?>(\s*<\/path>)?/g);
  const widthMatch = svgContent.match(/width="([^"]+)"/);
  const heightMatch = svgContent.match(/height="([^"]+)"/);

  if (!viewBoxMatch || !pathMatches) {
    console.error(`Skipping ${file}: missing viewBox or path`);
    return;
  }

  const viewBox = viewBoxMatch[1];
  const svgWidth = widthMatch ? parseFloat(widthMatch[1]) : 7;
  const svgHeight = heightMatch ? parseFloat(heightMatch[1]) : 5;
  const aspectRatio = svgHeight / svgWidth;
  const defaultSize = svgWidth * 3; // 7 → 21

  const paths = pathMatches
    .join('\n    ')
    .replace(/fill="[^"]*"/g, 'fill="@Color"');

  const razor = `<svg width="@_width" height="@_height" viewBox="${viewBox}" fill="none" xmlns="http://www.w3.org/2000/svg">
    ${paths}
</svg>

@code {
    [Parameter] public string Color { get; set; } = "currentColor";
    [Parameter] public int Size { get; set; } = ${defaultSize};

    private string _width => $"{Size}px";
    private string _height => System.FormattableString.Invariant($"{Size * ${aspectRatio}:F2}px");
}
`;

  fs.writeFileSync(path.join(outputFolder, `${componentName}.razor`), razor);
  console.log(`Generated ${componentName}.razor`);
});

console.log(`\n✅ ${files.length} icons converted to Blazor components`);
