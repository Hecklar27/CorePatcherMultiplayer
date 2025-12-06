#!/usr/bin/env python3
"""
C# File Collector
Collects all .cs files from the current directory and subdirectories
and copies them to a new folder for easy uploading.
"""

import os
import shutil
from pathlib import Path
from datetime import datetime


def collect_cs_files(source_dir='.', output_folder='collected_cs_files'):
    """
    Collects all .cs files from source_dir and its subdirectories
    and copies them to output_folder.
    
    Args:
        source_dir: Directory to search for .cs files (default: current directory)
        output_folder: Name of the folder to create for collected files
    """
    # Convert to Path objects
    source_path = Path(source_dir).resolve()
    output_path = source_path / output_folder
    
    # Create output folder if it doesn't exist
    output_path.mkdir(exist_ok=True)
    
    print(f"Searching for .cs files in: {source_path}")
    print(f"Output folder: {output_path}\n")
    
    # Find all .cs files recursively
    cs_files = list(source_path.rglob('*.cs'))
    
    # Exclude files that are already in the output folder
    cs_files = [f for f in cs_files if not str(f).startswith(str(output_path))]
    
    if not cs_files:
        print("No .cs files found!")
        return
    
    print(f"Found {len(cs_files)} .cs file(s)\n")
    
    # Track files with duplicate names
    name_counter = {}
    copied_count = 0
    
    # Copy each file
    for cs_file in cs_files:
        # Get the relative path from source for reference
        try:
            rel_path = cs_file.relative_to(source_path)
        except ValueError:
            rel_path = cs_file
        
        # Get the original filename
        original_name = cs_file.name
        
        # Handle duplicate filenames by adding a counter
        if original_name in name_counter:
            name_counter[original_name] += 1
            # Add the parent folder name and counter to make it unique
            name_without_ext = cs_file.stem
            parent_folder = cs_file.parent.name
            new_name = f"{name_without_ext}_{parent_folder}_{name_counter[original_name]}.cs"
        else:
            name_counter[original_name] = 0
            new_name = original_name
        
        # Destination path
        dest_path = output_path / new_name
        
        # Copy the file
        try:
            shutil.copy2(cs_file, dest_path)
            print(f"✓ Copied: {rel_path}")
            if new_name != original_name:
                print(f"  → Renamed to: {new_name}")
            copied_count += 1
        except Exception as e:
            print(f"✗ Error copying {rel_path}: {e}")
    
    print(f"\n{'='*60}")
    print(f"Summary:")
    print(f"  Total files found: {len(cs_files)}")
    print(f"  Successfully copied: {copied_count}")
    print(f"  Output location: {output_path}")
    print(f"{'='*60}")


if __name__ == "__main__":
    import sys
    
    # You can optionally pass a custom output folder name as a command-line argument
    output_folder = sys.argv[1] if len(sys.argv) > 1 else 'collected_cs_files'
    
    print("C# File Collector")
    print("="*60)
    print()
    
    try:
        collect_cs_files(output_folder=output_folder)
    except KeyboardInterrupt:
        print("\n\nOperation cancelled by user.")
    except Exception as e:
        print(f"\n\nAn error occurred: {e}")