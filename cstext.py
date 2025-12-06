#!/usr/bin/env python3
"""
C# File to Text Collector
Collects all .cs files from the current directory and subdirectories
and combines them into a single text document.
"""

import os
from pathlib import Path
from datetime import datetime


def collect_cs_to_text(source_dir='.', output_file='collected_cs_files.txt'):
    """
    Collects all .cs files from source_dir and its subdirectories
    and combines them into a single text file.
    
    Args:
        source_dir: Directory to search for .cs files (default: current directory)
        output_file: Name of the output text file
    """
    # Convert to Path objects
    source_path = Path(source_dir).resolve()
    output_path = source_path / output_file
    
    print(f"Searching for .cs files in: {source_path}")
    print(f"Output file: {output_path}\n")
    
    # Find all .cs files recursively
    cs_files = list(source_path.rglob('*.cs'))
    
    # Exclude the output file itself if it has a .cs extension (shouldn't, but just in case)
    cs_files = [f for f in cs_files if f != output_path]
    
    if not cs_files:
        print("No .cs files found!")
        return
    
    print(f"Found {len(cs_files)} .cs file(s)\n")
    
    # Sort files by path for consistent ordering
    cs_files.sort()
    
    processed_count = 0
    error_count = 0
    
    # Open the output file and write all .cs files to it
    try:
        with open(output_path, 'w', encoding='utf-8') as outfile:
            # Write header
            outfile.write("="*80 + "\n")
            outfile.write(f"C# Files Collection\n")
            outfile.write(f"Generated: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
            outfile.write(f"Total files: {len(cs_files)}\n")
            outfile.write("="*80 + "\n\n")
            
            # Process each .cs file
            for cs_file in cs_files:
                # Get the relative path from source for reference
                try:
                    rel_path = cs_file.relative_to(source_path)
                except ValueError:
                    rel_path = cs_file
                
                try:
                    # Write file header
                    outfile.write("\n" + "="*80 + "\n")
                    outfile.write(f"File: {rel_path}\n")
                    outfile.write(f"Full path: {cs_file}\n")
                    outfile.write("="*80 + "\n\n")
                    
                    # Read and write the file contents
                    with open(cs_file, 'r', encoding='utf-8') as infile:
                        content = infile.read()
                        outfile.write(content)
                    
                    # Add spacing after file content
                    if not content.endswith('\n'):
                        outfile.write('\n')
                    outfile.write('\n')
                    
                    print(f"✓ Added: {rel_path}")
                    processed_count += 1
                    
                except Exception as e:
                    print(f"✗ Error reading {rel_path}: {e}")
                    outfile.write(f"\n[ERROR: Could not read file - {e}]\n\n")
                    error_count += 1
            
            # Write footer
            outfile.write("\n" + "="*80 + "\n")
            outfile.write("End of Collection\n")
            outfile.write("="*80 + "\n")
        
        print(f"\n{'='*60}")
        print(f"Summary:")
        print(f"  Total files found: {len(cs_files)}")
        print(f"  Successfully processed: {processed_count}")
        print(f"  Errors: {error_count}")
        print(f"  Output file: {output_path}")
        print(f"  File size: {output_path.stat().st_size:,} bytes")
        print(f"{'='*60}")
        
    except Exception as e:
        print(f"✗ Error creating output file: {e}")


if __name__ == "__main__":
    import sys
    
    # You can optionally pass a custom output filename as a command-line argument
    output_file = sys.argv[1] if len(sys.argv) > 1 else 'collected_cs_files.txt'
    
    print("C# File to Text Collector")
    print("="*60)
    print()
    
    try:
        collect_cs_to_text(output_file=output_file)
    except KeyboardInterrupt:
        print("\n\nOperation cancelled by user.")
    except Exception as e:
        print(f"\n\nAn error occurred: {e}")
